# Covenant Unicode 17 provenance

The Covenant compiler policy v1 tables were generated solely from the checksum-verified Unicode 17.0.0 inputs below. Product and test execution never download or inspect host Unicode data.

| Input | Official URL | SHA-256 | Bytes |
|---|---|---|---:|
| `UnicodeData.txt` | https://www.unicode.org/Public/17.0.0/ucd/UnicodeData.txt | `2e1efc1dcb59c575eedf5ccae60f95229f706ee6d031835247d843c11d96470c` | 2198209 |
| `DerivedNormalizationProps.txt` | https://www.unicode.org/Public/17.0.0/ucd/DerivedNormalizationProps.txt | `71fd6a206a2c0cdd41feb6b7f656aa31091db45e9cedc926985d718397f9e488` | 1377582 |
| `NormalizationTest.txt` | https://www.unicode.org/Public/17.0.0/ucd/NormalizationTest.txt | `5019ffd530751a741900c849c0e010332f142a3612234639bd200b82138a87db` | 2827429 |

Generator version: `1`

Deterministic command:

```bash
python3 scripts/generate-covenant-unicode17.py --input-dir <verified-ucd-directory> --repo-root .
```

Generated artifacts:

- `CovenantUnicode17Tables.g.cs`: 135951 bytes
- `NormalizationTest.nfc.bin`: 815612 bytes, 20034 complete official conformance rows
- canonical decomposition records: 2081
- nonzero canonical combining-class records: 968
- full composition exclusions applied: 1120
- eligible composition pairs: 961
- Unicode `Cf` scalars: 170, compressed to 21 ranges

The Unicode inputs and generated tables are licensed under `Unicode-3.0`. The required copyright and permission notice is preserved in `LICENSE-Unicode-3.0.txt`, downloaded from https://www.unicode.org/license.txt with SHA-256 `e7a93b009565cfce55919a381437ac4db883e9da2126fa28b91d12732bc53d96`.
