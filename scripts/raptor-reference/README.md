# RAPTOR numerical reference fixtures

This is a development-only oracle for the managed RAPTOR implementation. Normal .NET builds and tests read the checked-in JSON and never execute Python. The production binary has no Python dependency. All inputs are small synthetic arrays; no installation data, model, or provider is accessed.

The official RAPTOR baseline is commit `7da1d48a7e1d7dec61a63c9d9aae84e2dfaa5767`, specifically [`raptor/cluster_utils.py`](https://github.com/parthsarthi03/raptor/blob/7da1d48a7e1d7dec61a63c9d9aae84e2dfaa5767/raptor/cluster_utils.py). The generator downloads and checks its exact SHA-256. It calls the installed official UMAP 0.5.5 and sklearn 1.4.2 numerical kernels rather than copying Arcanum's implementation. RAPTOR's own environment leaves sklearn unpinned and does not seed UMAP: these fixtures are an explicitly pinned independent kernel oracle, not a claim of reproducible upstream end-to-end layouts or memberships.

## Reproduce in an isolated environment

Use CPython **3.11.16**, uv **0.13.5**, and an empty scratch directory. The original verified interpreter was `/Users/mat/.local/bin/python3.11`. Nothing is installed globally or into the user's package environment. Run from the repository root (commands use Bash-compatible syntax):

```bash
RAPTOR_SCRATCH="$(mktemp -d /private/tmp/arcanum-raptor-reference.XXXXXX)"
RAPTOR_PYTHON=/Users/mat/.local/bin/python3.11
RAPTOR_UV=/opt/homebrew/bin/uv
"$RAPTOR_PYTHON" --version
"$RAPTOR_UV" --version
"$RAPTOR_UV" --no-config venv --python "$RAPTOR_PYTHON" "$RAPTOR_SCRATCH/venv"
"$RAPTOR_UV" --no-config pip install --python "$RAPTOR_SCRATCH/venv/bin/python" \
  --cache-dir "$RAPTOR_SCRATCH/uv-cache" --require-hashes --no-deps \
  -r scripts/raptor-reference/build-tools.lock
"$RAPTOR_UV" --no-config pip sync --python "$RAPTOR_SCRATCH/venv/bin/python" \
  --cache-dir "$RAPTOR_SCRATCH/uv-cache" --require-hashes --no-build-isolation \
  scripts/raptor-reference/requirements.lock
"$RAPTOR_SCRATCH/venv/bin/python" scripts/raptor-reference/generate.py \
  --artifacts "$RAPTOR_SCRATCH/artifacts" --prepare
"$RAPTOR_SCRATCH/venv/bin/python" scripts/raptor-reference/generate.py \
  --artifacts "$RAPTOR_SCRATCH/artifacts" --output "$RAPTOR_SCRATCH/fixtures"
diff -ru tests/RetroDownfall.Arcanum.Tests/Fixtures/Raptor "$RAPTOR_SCRATCH/fixtures"
```

The preparation step needs network access to PyPI and the exact official RAPTOR source. Generation after preparation uses only verified local artifacts and the isolated environment. Single-thread environment limits are set before importing numerical libraries. There are no timestamps or machine paths in the JSON. The manifest records the actual Python version and platform, source and kernel module hashes, generator/lock hashes, every selected package artifact's filename/URL/SHA-256, and each numeric fixture's SHA-256. A different platform may select different locked wheels and produce rounding differences; same-platform reproduction was checked byte-for-byte. Cross-architecture bit equality is not promised.

All proposed scientific versions resolved together without conflict: numpy 1.26.3, umap-learn 0.5.5, scikit-learn 1.4.2, scipy 1.12.0, numba 0.59.1, pynndescent 0.5.12. The fully pinned transitive closure adds llvmlite 0.42.0, joblib 1.4.2, threadpoolctl 3.5.0, tqdm 4.66.5. Source-only UMAP builds with pinned setuptools 75.8.0 and wheel 0.45.1, bootstrapped with hashes before `--no-build-isolation` synchronization. All 12 packages have upstream artifact hashes in `requirements.lock`; there are no floating requirements or implicit build-environment downloads.

To intentionally resolve a replacement lock, use the pinned interpreter and `uv --no-config pip compile scripts/raptor-reference/requirements.in --generate-hashes --no-header --no-annotate --output-file /private/tmp/raptor-requirements.lock --python "$RAPTOR_SCRATCH/venv/bin/python" --cache-dir "$RAPTOR_SCRATCH/uv-cache"`. Review the entire closure and update the two-package bootstrap lock to the same setuptools/wheel entries. Changing a dependency, numerical recipe, or source baseline requires a reviewed fixture recipe/version change, regenerated provenance, and independently checked outputs.

## Fixture meanings and limits

- `gaussian.json`: float64 SciPy multivariate-normal log densities, independently cross-checked against sklearn's full-covariance log-density kernel; covariance Cholesky factors/log determinants; normalized weighted posteriors and strict `p > 0.1` memberships. The diagonal analytic anchor has `L=diag(2,3)`, determinant 36 and log density `-log(12*pi)` at its mean. Three converged EM fits (K=1,2,3) include every initial weight/mean/covariance, final parameters, posterior arrays, likelihood and BIC. These are small kernel fixtures, not the full production BIC candidate search.
- `fuzzy-graph.json`: independent exact cosine neighbor arrays with self first and original-index tie breaks, fed to official `smooth_knn_dist`, `compute_membership_strengths` and `fuzzy_simplicial_set`. Cases cover separate duplicate identities, fractional fuzzy weights, all-identical points and disconnected components. UMAP skips column-zero self in calibration and writes zero diagonal membership; zero-distance duplicates remain distinct indices. Calibration and weights are official float32; compare the managed double outputs at absolute tolerance `1e-6`.
- `optimizer-step.json`: actual single-epoch UMAP Euclidean optimizer output, including scheduling, initial/final RNG state, coordinates, displacement and each edge's explicit negative-sample index tape. The closed-form attraction case uses a=b=1, alpha=0.25 and moves `(0,0),(1,0)` to `(0.25,0),(0.75,0)` without negative sampling. The second case exercises attraction and four negative samples per edge with the fitted min_dist=0.1/spread=1.0 curve. Official coordinates and squared distances are float32; schedules are float64. These are fixed-initialization equation fixtures, not layout equality promises.

The managed recipe uses double arithmetic and stage-specific SplitMix64 streams. Official UMAP uses Tausworthe RNG for negative sampling. Later optimizer tests should supply the exported negative-sample tape to isolate the equations; they must separately test the managed stream and scheduling. Do not silently replace production SplitMix64 with upstream random state to obtain coordinate equality. Curve coefficients are exported from the pinned official fit; later code must include every added behavioral constant in its algorithm fingerprint.

The .NET fixture reader uses an explicit generated JSON context and validates supported recipe, complete pinned dependency closure, source identity, hashes and numeric artifact integrity before serving fixture values. It rejects incomplete or corrupt provenance. Numeric implementation/golden comparisons belong to their subsequent stages; stage 1 establishes the oracle and identity boundary only.

## Upstream licenses

No upstream implementation source is vendored or executed in the shipped binary by this stage. Reference generation uses RAPTOR ([MIT](https://github.com/parthsarthi03/raptor/blob/7da1d48a7e1d7dec61a63c9d9aae84e2dfaa5767/LICENSE.txt)), umap-learn ([BSD-3-Clause](https://github.com/lmcinnes/umap/blob/release-0.5.5/LICENSE.txt)), scikit-learn ([BSD-3-Clause](https://github.com/scikit-learn/scikit-learn/blob/1.4.2/COPYING)), and SciPy ([BSD-3-Clause](https://github.com/scipy/scipy/blob/v1.12.0/LICENSE.txt)). Numerical ports in later stages must carry the required attribution and license text with the ported source.
