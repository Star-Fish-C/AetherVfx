# Third-party notices

AVFX block layout, node tags, cumulative list serialization, equipment VFX paths, and ActorVFX create/remove interop signatures and lifetime tracking were implemented with reference to Dalamud-VFXEditor:
https://github.com/0ceal0t/Dalamud-VFXEditor

Copyright 2021 Michael Kaminsky

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Penumbra.Api is an external build/runtime dependency. Its license is supplied with the distribution; see https://github.com/Ottermandias/Penumbra.Api . Game assets are not included in source distributions.

The bundled bnpc.json and npc_files.json metadata snapshots are from Dalamud-VFXEditor commit 05f6eb0cce19a6a91ae275bce609299d573914b3, under the above repository license. The upstream BNpc naming mapping cites ffxiv-teamcraft's gubal-bnpcs-index.json. These are reference metadata, not game asset binaries. catalog-cache.json.gz is generated locally from the installed game and the attributed metadata; it contains identifiers and associations, not AVFX/texture payloads.

Native CharaView API reference: https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/UI/Misc/CharaView.cs . FFXIVClientStructs is supplied by Dalamud and is not copied into this package.
