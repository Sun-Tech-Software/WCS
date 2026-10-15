# Third-party notices

The Sun Tech WCS is licensed under the [Apache License, Version 2.0](LICENSE). This file lists the
third-party software, fonts and images that are **copied into this repository**, with their
licences and the notices those licences ask to keep. The full licence texts are in
[`licenses/`](licenses/) (Apache-2.0 is [`LICENSE`](LICENSE)). The same inventory, together with
every NuGet package the projects restore, is in the CycloneDX SBOM
[`sbom/wcs.cdx.json`](sbom/wcs.cdx.json).

This inventory was compiled from the files themselves (their banners, headers and version strings)
and is offered as information. It is not a legal opinion and does not state that the distribution
complies with every licence: the points that still need a legal review are listed at the end.

## NuGet packages (not stored in the repository)

The projects reference NuGet packages that are restored from nuget.org at build time and are not
part of this repository. Each package is distributed under its own licence, stated in its
`.nuspec`. The SBOM lists every package (direct and transitive) with that licence and marks with
scope `excluded` the packages used only by test projects.

## Components copied into the repository

| Component | Version | Location | Licence | Licence text |
|---|---|---|---|---|
| draw.io | 31.1.2 | `WCS/Wcs.WebEditor/src/wwwroot/drawio` | Apache-2.0 | [LICENSE](LICENSE) |
| mxGraph (shipped with draw.io) | 31.1.2 | `drawio/mxgraph` | Apache-2.0 | [LICENSE](LICENSE) |
| DOMPurify | 3.4.12 | `drawio/js/app.min.js` | Apache-2.0 OR MPL-2.0 (Apache-2.0 elected) | [LICENSE](LICENSE) |
| pako | 2.2.0 | `drawio/js/app.min.js` (an older copy inside JSZip) | MIT AND Zlib | [pako-LICENSE.txt](licenses/pako-LICENSE.txt), [pako-Zlib.txt](licenses/pako-Zlib.txt) |
| CryptoJS | 3.1.2 | `drawio/js/app.min.js` | BSD-3-Clause | [CryptoJS-LICENSE.txt](licenses/CryptoJS-LICENSE.txt) |
| spin.js | 2.0.0 | `drawio/js/app.min.js` | MIT | [spin.js-LICENSE.txt](licenses/spin.js-LICENSE.txt) |
| Rough.js | not stated | `drawio/js/app.min.js` | MIT | [roughjs-LICENSE.txt](licenses/roughjs-LICENSE.txt) |
| JSZip | 3.10.0 | `drawio/js/extensions.min.js` | MIT OR GPL-3.0-or-later (MIT elected) | [JSZip-LICENSE.markdown](licenses/JSZip-LICENSE.markdown) |
| @drawio/elk (Eclipse Layout Kernel) | 0.1.0+commit.184adf1 | `drawio/js/extensions.min.js` | EPL-2.0 | [EPL-2.0.txt](licenses/EPL-2.0.txt) |
| @drawio/mermaid | 0.1.0+commit.a23de8e | `drawio/js/extensions.min.js` | not stated (see review) | - |
| dagre, @dagrejs/graphlib | dagre 3.0.0, graphlib 4.0.1 and 2.1.9-pre | `drawio/js/extensions.min.js` | MIT | [dagre-graphlib-LICENSE.txt](licenses/dagre-graphlib-LICENSE.txt) |
| lodash (parts, inside the dagre build) | not stated | `drawio/js/extensions.min.js` | MIT | [lodash-LICENSE.txt](licenses/lodash-LICENSE.txt) |
| layout-base, cose-base | not stated | `drawio/js/extensions.min.js` | MIT | [layout-base-LICENSE.txt](licenses/layout-base-LICENSE.txt), [cose-base-LICENSE.txt](licenses/cose-base-LICENSE.txt) |
| Bridge.NET | 15.6.0 | `drawio/js/extensions.min.js` | Apache-2.0 | [LICENSE](LICENSE) |
| OrgChart.Layout (compiled with Bridge.NET) | not stated | `drawio/js/extensions.min.js` | not stated (see review) | - |
| libavoid (Adaptagrams), via libavoid-js | libavoid-js 0.5.0-beta.5 | `drawio/js/extensions.min.js` | LGPL-2.1-or-later | [LGPL-2.1.txt](licenses/LGPL-2.1.txt) |
| WebCola | not stated | `drawio/plugins/webcola/cola.min.js` | MIT | [WebCola-LICENSE.txt](licenses/WebCola-LICENSE.txt) |
| Architects Daughter (font) | 1.003 | `drawio/styles/fonts/ArchitectsDaughter-Regular.ttf` | OFL-1.1 | [OFL-1.1.txt](licenses/OFL-1.1.txt) |
| draw.io icon sets and stencil libraries | 31.1.2 | `drawio/img`, `drawio/images`, `drawio/js/stencils.min.js`, `drawio/js/shapes-14-6-5.min.js` | Apache-2.0 with the restriction of `drawio/img/LICENSE` | [LICENSE](LICENSE), `drawio/img/LICENSE` |
| net-object-deep-copy | not stated | `Common/Helper` (four files) | MIT | [net-object-deep-copy-LICENSE.txt](licenses/net-object-deep-copy-LICENSE.txt) |

`drawio/` stands for `WCS/Wcs.WebEditor/src/wwwroot/drawio/`.

## Notices

**draw.io and mxGraph.** Copyright (c) 2006-2025, JGraph Holdings Ltd; Copyright (c) 2006-2025,
draw.io AG. Licensed under the Apache License, Version 2.0. <https://github.com/jgraph/drawio>.
The copyright lines are those of the file headers (`js/PreConfig.js`, `js/bootstrap.js`,
`mxgraph/src/*.js`, the plugins). Two files of the folder are Sun Tech's: `plugins/kazam.js` was
written by Sun Tech Srl, and **`js/bootstrap.js` was modified by Sun Tech Srl** (it registers
`plugins/kazam.js` among the plugins draw.io may load); both are covered by this product's licence.
The rest of the folder is redistributed as received from the draw.io release.

**Icon sets and stencil libraries of draw.io.** They carry an additional restriction, kept in
`drawio/img/LICENSE`: they, and anything derived from them, may not be used as software assets in,
distributed for use with, or incorporated into Atlassian products or products distributed through
the Atlassian marketplace or plugin ecosystem without explicit written permission. The restriction
does not apply to diagrams created with the software. Many of these sets reproduce logos and
product icons of other companies, which remain trademarks of their owners.

**DOMPurify 3.4.12.** (c) Cure53 and other contributors. Released under the Apache License 2.0 and
Mozilla Public License 2.0 (this repository uses it under Apache-2.0).
<https://github.com/cure53/DOMPurify>

**pako 2.2.0.** Copyright (C) 2014-2017 by Vitaly Puzrin and Andrei Tuputcyn, MIT License. The
parts ported from zlib: (C) 1995-2013 Jean-loup Gailly and Mark Adler, zlib License.
<https://github.com/nodeca/pako>

**CryptoJS 3.1.2.** (c) 2009-2013 by Jeff Mott. All rights reserved. New BSD License.
<https://code.google.com/archive/p/crypto-js/>

**spin.js 2.0.0.** Copyright (c) 2011-2014 Felix Gnass, MIT License. <https://github.com/fgnass/spin.js>

**Rough.js.** Copyright (c) 2019 Preet Shihn, MIT License. <https://github.com/rough-stuff/rough>

**JSZip 3.10.0.** (c) 2009-2016 Stuart Knightley, David Duponchel, Franz Buchinger, António
Afonso. Dual licensed under the MIT license or GPLv3; used here under the MIT license.
<https://github.com/Stuk/jszip>

**Eclipse Layout Kernel (in @drawio/elk 0.1.0).** The layout algorithms of the Eclipse Layout
Kernel (`org.eclipse.elk`), Copyright (c) Kiel University and others, are made available under the
Eclipse Public License 2.0 (`licenses/EPL-2.0.txt`). They are redistributed unmodified, as part of
draw.io, in compiled JavaScript form. The source code is available from the Eclipse Foundation at
<https://github.com/eclipse/elk> (and, for the JavaScript build, <https://github.com/kieler/elkjs>).

**dagre and graphlib.** Copyright (c) 2012-2014 Chris Pettitt, MIT License.
<https://github.com/dagrejs/dagre>. Their build includes parts of **lodash**, Copyright OpenJS
Foundation and other contributors, MIT License. <https://github.com/lodash/lodash>

**layout-base and cose-base.** Copyright (c) 2019 - present, iVis@Bilkent, MIT License.
<https://github.com/iVis-at-Bilkent>

**Bridge.NET 15.6.0.** Copyright 2008-2016 Object.NET, Inc. Licensed under the Apache License 2.0.
<https://github.com/bridgedotnet/Bridge>

**libavoid.** Part of Adaptagrams, Copyright (C) Monash University and its contributors, licensed
under the GNU Lesser General Public License, version 2.1 or later (`licenses/LGPL-2.1.txt`). It is
included in `drawio/js/extensions.min.js`, compiled to JavaScript (libavoid-js 0.5.0-beta.5, built
with Emscripten), unmodified, as part of draw.io. The source code of libavoid is available at
<https://github.com/mjwybrow/adaptagrams>; that of the JavaScript build is published as the npm
package `libavoid-js`. The Emscripten runtime that wraps it is distributed by the
Emscripten authors under the MIT or the University of Illinois/NCSA licence.

**WebCola.** Copyright (c) 2013 Tim Dwyer, MIT License. <https://github.com/tgdwyer/WebCola>

**Architects Daughter.** Copyright (c) 2010, Kimberly Geswein (kimberlygeswein.com). Licensed under
the SIL Open Font License, Version 1.1 (`licenses/OFL-1.1.txt`).

**net-object-deep-copy.** Copyright (c) 2014 Burtsev Alexey, MIT License.
<https://github.com/Burtsev-Alexey/net-object-deep-copy>. `ObjectExtensions.Copy` and its helpers
in `Common/Helper` (`ObjectExtensions.cs`, `ArrayExtensions.cs`, `ArrayTraverse.cs`,
`ReferenceEqualityComparer.cs`) are derived from it and were modified by Sun Tech Srl
(namespaces, nullable annotations, layout); each file carries the attribution.

## Referenced but not distributed

- draw.io can load at run time scripts that are **not** in this repository: Google APIs
  (`apis.google.com`), MathJax (`math4/`), the Miro importer and `js/diagramly/Extensions.js`.
  They are not distributed here; the WCS web editor does not need them.
- The ASP.NET Core and Blazor framework files the host serves come from the .NET SDK and NuGet,
  not from this repository.
- `WCS/tools/doc/*.js` (documentation helpers) `require` npm packages (`pptxgenjs`, `marked`) that
  the user installs; `rendi-pdf.js` produces pages that load Mermaid from a CDN. None of them is
  bundled.

## Sources of the licence texts in `licenses/`

| File | Taken from |
|---|---|
| `EPL-2.0.txt` | `LICENSE.md` of the npm package elkjs 0.11.1 (the JavaScript build of ELK), verbatim |
| `LGPL-2.1.txt` | `COPYING.LGPLv2.1` shipped by Git for Windows (xz), verbatim: the FSF text of the LGPL 2.1 |
| `OFL-1.1.txt` | the font's copyright line, then the OFL 1.1 text of a local copy of Source Sans Pro's `LICENSE` (in a VS Code extension), without its Adobe header |
| `JSZip-LICENSE.markdown` | `LICENSE.markdown` of JSZip 3.10.1, verbatim |
| `pako-LICENSE.txt` | `LICENSE` of pako 1.0.11, verbatim (same copyright line as 2.2.0 to the best of our knowledge) |
| `pako-Zlib.txt` | the zlib licence as shipped by Git for Windows (zlib `LICENSE`), with the copyright lines of the pako sources |
| `roughjs-LICENSE.txt`, `lodash-LICENSE.txt`, `layout-base-LICENSE.txt`, `cose-base-LICENSE.txt` | `LICENSE` of roughjs 4.6.6, lodash 4.18.1, layout-base 1.0.2, cose-base 1.0.3, verbatim |
| `spin.js-LICENSE.txt`, `WebCola-LICENSE.txt`, `dagre-graphlib-LICENSE.txt`, `net-object-deep-copy-LICENSE.txt` | the standard MIT text (as in roughjs 4.6.6) with the copyright line of each project |
| `CryptoJS-LICENSE.txt` | the standard BSD-3-Clause text with Jeff Mott's notice, reconstructed |

The npm packages above were read from local copies already on the build machine; nothing was
downloaded to write this file. Where the text comes from a different version than the one
bundled, the licence is the same but the copyright years may differ.

## Items requiring legal review

1. **libavoid under LGPL-2.1, inside a minified bundle.** `drawio/js/extensions.min.js` contains
   libavoid compiled to JavaScript. The previous NOTICE did not list it and the bundle states no
   licence for it. LGPL-2.1 asks, among other things, that the recipient can obtain the source of
   the library and replace or modify it (section 6). Whether shipping draw.io's prebuilt bundle with
   pointers to the sources satisfies those obligations, or whether the library should be kept as a
   separate file (or removed, since the WCS editor may not need draw.io's libavoid routing), needs
   a decision. The Emscripten runtime licence text (MIT or NCSA) is not included.
2. **EPL-2.0 obligations for ELK.** EPL-2.0 section 3.2 asks that a distribution in object code
   form state that the source code is available and how to obtain it, and that the EPL notices are
   not removed. The bundle has no EPL notice; this file supplies the statement and the text. To be
   confirmed: that redistribution as part of draw.io in compiled form, under this notice, is
   enough, and which ELK release @drawio/elk 0.1.0 was built from.
3. **dagre's missing LEGAL file.** The bundle says "For license information please see
   dagre.min.js.LEGAL.txt" and that file is not part of the draw.io release we copied. The notice
   above is reconstructed from the dagre project, not from the missing file, which may list more
   components.
4. **Licences not stated in the bundle.** @drawio/mermaid (and whether it contains Mermaid code,
   MIT, Knut Sveidqvist), OrgChart.Layout (author and licence unknown), Rough.js, layout-base,
   cose-base, lodash, WebCola (`cola.min.js` has no banner), libavoid: identified by their code, not
   by a notice in the files. The versions marked "not stated" could not be established.
5. **Reconstructed licence texts.** CryptoJS (the bundle points to a Google Code page that no
   longer exists), spin.js, WebCola, dagre/graphlib: standard texts with the projects' copyright
   lines, not copies of the components' own files.
6. **The stencil restriction.** `drawio/img/LICENSE` restricts the icon sets and stencils with
   respect to Atlassian products. It is kept in place and repeated above; whether the restriction
   is compatible with presenting the folder as Apache-2.0, and what it means for downstream users
   of this repository, needs a review. The vendor logos and product icons in the sets (AWS, Azure,
   Google Cloud, Cisco, IBM, SAP, Atlassian, Dropbox, Google Drive and others) are trademarks of
   their owners.
7. **The font.** `ArchitectsDaughter-Regular.ttf` states its copyright but no licence. OFL-1.1 is
   the licence under which it is distributed by Google Fonts; to be confirmed for the copy draw.io
   ships, and the OFL asks that the font is not sold by itself and that the Reserved Font Name
   rules are respected.
8. **The modified draw.io file.** `drawio/js/bootstrap.js` was changed by Sun Tech; Apache-2.0
   section 4(b) asks that modified files carry prominent notices that they were changed. The
   change is stated here; a notice in the file itself may be required.
9. **DOMPurify, JSZip dual licences.** This file elects Apache-2.0 for DOMPurify and MIT for JSZip;
   the election should be confirmed.
10. **NuGet packages.** The SBOM reads the licence of each package from its `.nuspec`; a few
    packages declare a licence file or URL instead of an SPDX expression and were not reviewed one
    by one.
