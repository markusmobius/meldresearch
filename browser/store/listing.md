# Chrome Web Store listing copy

Prepared for a **private tester-group release**, version 0.5.0. Publisher: **Meld Research**.
Public support and privacy contact: **markusmobius@gmail.com**.
This document is submission material, not an assertion that a listing exists.

## Name

Meld Research - Overleaf connection

## Short description

Read selected Overleaf projects into your local Meld Research application. No cookie extraction.

## Detailed description

Connect your Overleaf research to Meld Research on your own computer, using the
Overleaf account already signed in to your normal Chrome profile.

This read-only connector:

- Connects your browser to the local Meld Research desktop companion.
- Lets Meld display your Overleaf project list after connection, reconnection,
  or sign-in recovery.
- Downloads explicitly selected projects, including automatic checks for projects
  whose monitoring the user enables in the desktop companion.
- Reuses a normal Overleaf tab, or opens a background tab when a read needs one.
- Shows only Sign in to Overleaf until you are signed in, then Connect or Disconnect.
- Remembers Disconnect across browser restarts.

Your Overleaf originals are not edited. The connector does not read or decrypt
browser cookie databases, export authentication cookies, or ask for your Overleaf
password. If you link a Meld account and explicitly enable monitoring, the companion
sends selected text through Meld's remote service to Azure AI Foundry for research
updates. This is not entirely on-device processing.

The extension handles Overleaf website content locally: project metadata and
selected ZIP contents are transferred to the Meld Research companion on the same
computer. Indexed research is encrypted locally; manual ZIP exports remain ordinary
files. Disabling or uninstalling the extension does not delete companion data.
There is no advertising, unrelated analytics, or sale of research content.

REQUIREMENTS

This is a companion extension, not a standalone Overleaf downloader. A compatible
Meld Research desktop companion and its registered native-messaging host must be
installed and running. Chrome 120 or later and a normal, non-incognito Overleaf
session are required. A free Overleaf account with an accessible project is
sufficient for the connector's functions.

The first test release targets Windows. macOS registration code exists, but
macOS distribution has not yet been live-verified. Firefox, Safari, self-hosted
Overleaf, editing, and live collaboration are not
supported.

The Overleaf web interface is unofficial and may change. Login challenges and
rate limits remain subject to Overleaf's normal rules. Meld Research is not
affiliated with or endorsed by Overleaf or Google.

Support and privacy questions: markusmobius@gmail.com

## Dashboard fields

- Visibility: **Private**, with the publisher-managed Google Group selected under
  Distribution. The account-level Trusted tester accounts field is for individual
  Google accounts. Unlisted is not group access control.
- Language: English.
- Select the closest available research/productivity category in the dashboard.
- Homepage/download URL: not provided yet. Do not use a placeholder URL.
- Privacy policy URL: `https://www.meldresearch.org/privacy.html`. The remote backend
  serves the bundled notice at `/privacy.html`; actual public hosting/DNS/HTTPS
  must be completed before review.
- Support URL/email: `mailto:markusmobius@gmail.com` where email links are accepted;
  supply a real HTTPS support page if the dashboard requires an HTTPS URL.

## Single purpose

Provide a read-only connection from a user's signed-in Overleaf browser session
to the Meld Research desktop companion on the same computer, so the user can
list projects, save selected snapshots, and monitor chosen research projects for
meaningful updates through the companion's explicitly authorized Foundry processing.

## Permission justifications

| Permission | Justification |
| --- | --- |
| `nativeMessaging` | Exchanges bounded project metadata and user-selected ZIP data with the installed Meld Research native host. Browser cookies and passwords are not exported. |
| `scripting` | Injects only packaged, read-only scripts into an Overleaf tab to make authenticated first-party HTTPS reads and filter metadata before it leaves that tab. |
| `storage` | Persists the user's Connect/Disconnect (auto-connect) preference in the local browser profile. It does not store project contents or authentication tokens. |
| `alarms` | Retries the local native-host connection while enabled, including after service-worker suspension or a desktop restart. The alarm itself does not read Overleaf. |
| `https://www.overleaf.com/*` | Finds and reuses an Overleaf tab, performs the project-list and selected-project ZIP reads, reads each included project's edit history keeping only file paths and last-edit times, detects completion of sign-in, and, while the popup is open, checks only whether Overleaf redirects to its sign-in page. No other remote website is granted access. |

External messaging is limited to the local Meld page on `localhost` or
`127.0.0.1`; the worker independently checks ports 6001/6010 and rejects subframes.
Website messages are control-only and cannot receive project data or supply
arbitrary URLs. Other extensions are not allowed as external callers.

## Data and remote-code declarations

- **Remote code: No.** All executed connector JavaScript is packaged in the ZIP.
  Overleaf HTML is parsed as data, not evaluated as extension code.
- **Website content: Yes.** Project metadata and selected document/archive
  contents are handled and transferred to the companion locally.
- **Web history / browsing activity: disclose the narrow Overleaf URL handling.**
  The worker checks matching Overleaf tab URLs for operation and sign-in
  recovery. It does not retain a general browsing history. Do not claim no URL
  handling merely because the `history` permission is absent.
- Overleaf authentication information is not extracted or exported. Chrome supplies
  its existing session cookies directly to Overleaf.
- The companion service now handles Google account identity (name, email and
  identifier), OAuth sign-in, and installation credentials. Review the personally
  identifiable information and authentication-information categories for the
  complete advertised service; the old blanket "no authentication information"
  statement no longer describes the product.
- Project archives may contain personal information or communications supplied
  by users. Describe this incidental content in the policy and review the
  dashboard's current category definitions before certifying the checkboxes.
- No advertising, sale of data, unrelated secondary use, lending use, or model
  training. The publisher must verify and personally certify the dashboard's
  limited-use statements.
- **Do not select "no user data collected/handled."** Chrome requires disclosure
  even when handling and storage are entirely local.

## Listing images

- `store-icon-128.png`: original Meld icon, 128x128 PNG with transparent padding.
- `small-promo-440x280.png`: original promotional artwork; no Overleaf or Google
  logo is used.
- `privacy-screenshot-1280x800.png`, when generated: a genuine screenshot of the
  bundled privacy page, not evidence of a completed Overleaf connection.
- Before submission, also capture the working connector/project-download
  workflow using a synthetic, non-private Overleaf project. Do not upload
  screenshots of personal research, cookies, account details, or this chat.

See `submission.md` for the gates that must be resolved before review.
