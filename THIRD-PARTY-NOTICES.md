# Third-party notices

PDF4Free is distributed together with the components below. Each remains under its own licence; the authoritative
licence text of every component is at the link given for it.

## PDFium

Used for rendering, text extraction and content-object editing. Shipped as `pdfium.dll`.

- Project: https://pdfium.googlesource.com/pdfium/
- Licence: BSD-3-Clause and Apache-2.0 — https://pdfium.googlesource.com/pdfium/+/refs/heads/main/LICENSE
- Copyright 2014 The PDFium Authors

PDFium itself includes other open-source libraries (among them FreeType, libjpeg-turbo, OpenJPEG, Little CMS,
libpng, zlib, ICU and Abseil), each under its own licence as listed in the PDFium source tree under `third_party/`.

The binary comes from the `bblanchon.PDFium.Win32` NuGet package (https://github.com/bblanchon/pdfium-binaries),
whose packaging is under the MIT licence.

```
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * Neither the name of Google LLC nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## PDFsharp and MigraDoc 6.2.4

Used for writing and restructuring PDF files and for turning text into PDF.

- Project: https://github.com/empira/PDFsharp — https://docs.pdfsharp.net
- Licence: MIT (this applies to version 6.2.4, the version PDF4Free uses; later versions are licensed differently)
- Copyright (c) 2001-2024 empira Software GmbH, Troisdorf (Cologne Area), Germany

## .NET runtime, Windows Presentation Foundation, System.Speech, Microsoft.Extensions.Logging.Abstractions

The application platform; the installer contains the parts of the .NET 10 runtime the application needs.

- Project: https://github.com/dotnet
- Licence: MIT — https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- Copyright (c) .NET Foundation and Contributors

## Microsoft Edge WebView2 SDK

Used to turn web pages into PDF. The WebView2 *runtime* is not included; it is part of Windows.

- Project: https://developer.microsoft.com/microsoft-edge/webview2/
- Licence: Microsoft WebView2 SDK licence (BSD-style) — https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4370.26/License
- Copyright (C) Microsoft Corporation

## The MIT licence

The components marked "MIT" above are provided under these terms:

```
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
```

## Fonts and system services

PDF4Free embeds only the fonts already installed on the user's PC, in the documents the user edits. Text
recognition (OCR), scanning (WIA) and speech are Windows services and are not redistributed.
