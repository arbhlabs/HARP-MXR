#!/usr/bin/env python3
"""Dependency-free structural checks only. NOT a C# compiler or audio runtime test."""
from pathlib import Path
import xml.etree.ElementTree as ET
import json
import hashlib

root = Path(__file__).resolve().parent.parent
files = list(root.rglob('*'))
cs = list(root.rglob('*.cs'))
assert len(cs) >= 10, 'Missing source files'
project = ET.parse(root / 'src/MixDock/MixDock.csproj')
refs = project.findall('.//PackageReference')
assert any(r.attrib.get('Include') == 'NAudio' and r.attrib.get('Version') == '3.2.0' for r in refs)
assert 'net9.0-windows' in (root / 'src/MixDock/MixDock.csproj').read_text()
assert (root / 'AGENT_HANDOFF.md').is_file()
assert (root / 'docs/WINDOWS_VALIDATION_RESULTS.md').is_file()
assert (root / '.github/workflows/windows.yml').is_file()
source = '\n'.join(f.read_text(encoding='utf-8') for f in cs)
for marker in ('WithProcessLoopback(', 'WasapiPlayerBuilder(', 'Read(Span<float>',
               'XInputGetState', 'RegisterHotKey', 'Crossfader', 'DeckDsp'):
    assert marker in source, marker
assert 'NOT EXECUTED' in (root / 'docs/WINDOWS_VALIDATION_RESULTS.md').read_text()
print(f'Structural audit PASS — {len(cs)} C# files, NAudio pinned at 3.2.0.')
print('Not a build or Windows smoke test. Execute scripts/build.ps1 on Windows.')
