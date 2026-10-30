using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Middleware;

namespace MeldResearch.LocalBackend.Services;

internal static partial class ProjectParser
{
    [GeneratedRegex("^[a-fA-F0-9]{24}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectIdPattern();

    public static bool IsProjectId(string? value) => value is not null && ProjectIdPattern().IsMatch(value);

    public static async Task<IReadOnlyList<OverleafProjectResponse>> ParseAsync(Stream metadata, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await JsonDocument.ParseAsync(metadata, new JsonDocumentOptions
            {
                MaxDepth = 32,
                AllowDuplicateProperties = false
            }, cancellationToken);
            if (!json.RootElement.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array ||
                projects.GetArrayLength() > 10_000)
                throw new InvalidDataException("Invalid project list.");

            var results = new List<OverleafProjectResponse>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in projects.EnumerateArray())
            {
                string? id = project.GetProperty("id").GetString();
                string? name = project.GetProperty("name").GetString();
                bool archived = project.GetProperty("archived").GetBoolean();
                bool trashed = project.GetProperty("trashed").GetBoolean();
                if (!IsProjectId(id) || string.IsNullOrWhiteSpace(name) || name.Length > 1000 || !ids.Add(id!))
                    throw new InvalidDataException("Invalid project metadata.");

                DateTimeOffset? updatedAt = null;
                if (project.TryGetProperty("lastUpdated", out var updated) && updated.ValueKind != JsonValueKind.Null)
                {
                    if (!DateTimeOffset.TryParse(updated.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var timestamp))
                        throw new InvalidDataException("Invalid project timestamp.");
                    updatedAt = timestamp;
                }
                if (!archived && !trashed)
                    results.Add(new(id!.ToLowerInvariant(), name, updatedAt));
            }
            // Like Overleaf's own list: most recently updated first; undated projects last, then by name.
            return results.OrderByDescending(project => project.UpdatedAt.HasValue)
                .ThenByDescending(project => project.UpdatedAt)
                .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or
            KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new ApiFailure(502, "overleaf_metadata_changed", "Overleaf returned unsupported project metadata. No project list was replaced.");
        }
    }
}
