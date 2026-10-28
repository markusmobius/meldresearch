# Third-party notices

## pyoverleaf

The Overleaf project metadata and ZIP-download approach is adapted from
[jkulhanek/pyoverleaf](https://github.com/jkulhanek/pyoverleaf), inspected at commit
`7ce272244e6dc37160517b8eac3e30148d28f102`. The Python implementation and its browser
cookie extraction are not bundled.

MIT License

Copyright (c) 2023 Jonas Kulhanek

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Packaged dependencies

NuGet and npm dependencies retain their upstream licenses. Angular's production
build emits `3rdpartylicenses.txt`; retain it when distributing the built UI.

The UI uses Angular Material and the Outfit and Material Icons font packages.
Fonts are served locally, not requested from Google Fonts. Their license files
are included in the built UI's `font-licenses` directory and must be retained.
