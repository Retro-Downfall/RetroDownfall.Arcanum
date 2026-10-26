#!/usr/bin/env python3
"""Independent, dev-only RAPTOR numeric fixtures. Never imports the managed kernels."""
from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import re
import sys
import urllib.request

# Set before importing numpy/numba. No writes to user settings or package locations.
for variable in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "NUMBA_NUM_THREADS"):
    os.environ[variable] = "1"

COMMIT = "7da1d48a7e1d7dec61a63c9d9aae84e2dfaa5767"
REFERENCE_SHA256 = "bfe3fa5c5ddf6741b3d1fa1747203d6899c1520aa6d3d167cbab34445de78317"
REFERENCE_URL = f"https://raw.githubusercontent.com/parthsarthi03/raptor/{COMMIT}/raptor/cluster_utils.py"
RECIPE = "raptor-reference-v1"
SEED = 224
HERE = Path(__file__).resolve().parent


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def write_json(path: Path, document: dict) -> None:
    path.write_text(json.dumps(document, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def locked_packages() -> dict[str, tuple[str, set[str]]]:
    packages = {}
    for entry in re.split(r"\n(?=[a-z])", (HERE / "requirements.lock").read_text()):
        match = re.search(r"^([a-z0-9-]+)==([^\s]+)", entry, re.MULTILINE)
        if match:
            packages[match[1]] = (match[2], set(re.findall(r"--hash=sha256:([a-f0-9]{64})", entry)))
    if len(packages) != 12 or any(not hashes for _, hashes in packages.values()):
        raise ValueError("The complete 12-package closure and artifact hashes are required")
    return packages


def download(url: str) -> bytes:
    with urllib.request.urlopen(url, timeout=60) as response:
        return response.read()


def prepare(directory: Path) -> None:
    """Download source/wheels to an explicit scratch directory; verify every byte against the lock."""
    from wheel.vendored.packaging.tags import parse_tag, sys_tags
    from wheel.vendored.packaging.utils import parse_wheel_filename

    directory.mkdir(parents=True, exist_ok=True)
    supported = {tag: rank for rank, tag in enumerate(sys_tags())}
    artifacts = []
    for name, (version, hashes) in sorted(locked_packages().items()):
        release = json.loads(download(f"https://pypi.org/pypi/{name}/{version}/json"))
        candidates = []
        for item in release["urls"]:
            if item["packagetype"] == "bdist_wheel":
                tags = parse_wheel_filename(item["filename"])[3]
                rank = min((supported[tag] for tag in tags if tag in supported), default=None)
                if rank is not None:
                    candidates.append((rank, item["filename"], item))
            elif name == "umap-learn" and item["packagetype"] == "sdist":
                candidates.append((10**9, item["filename"], item))
        if not candidates:
            raise ValueError(f"No locked reference artifact for {name} on this platform")
        item = min(candidates)[2]
        expected = item["digests"]["sha256"]
        if expected not in hashes:
            raise ValueError(f"Unapproved PyPI artifact: {item['filename']}")
        path = directory / item["filename"]
        data = path.read_bytes() if path.exists() else download(item["url"])
        if sha256(data) != expected:
            raise ValueError(f"Corrupt reference artifact: {path.name}")
        path.write_bytes(data)
        artifacts.append(dict(name=name, version=version, filename=path.name, url=item["url"], sha256=expected))
    source = download(REFERENCE_URL)
    if sha256(source) != REFERENCE_SHA256:
        raise ValueError("Official RAPTOR source hash differs from the approved commit")
    (directory / "cluster_utils.py").write_bytes(source)
    write_json(directory / "artifacts.json", dict(artifacts=artifacts))
    print(f"Verified {len(artifacts)} package artifacts and official RAPTOR source in {directory}")


def gaussian_fixture() -> dict:
    import numpy as np
    from scipy.special import logsumexp
    from scipy.stats import multivariate_normal
    from sklearn.mixture import GaussianMixture
    from sklearn.mixture._gaussian_mixture import _compute_precision_cholesky, _estimate_log_gaussian_prob

    points = np.array([[-2, -1], [-1, 0], [0, 0], [1, 0], [2, 1]], dtype=np.float64)
    means = np.array([[-1, 0], [1, 0]], dtype=np.float64)
    covariance = np.array([[[2, 0.6], [0.6, 1]], [[2, -0.4], [-0.4, 1.5]]], dtype=np.float64)
    weights = np.array([0.5, 0.5])
    logpdf = np.column_stack([multivariate_normal.logpdf(points, mean=m, cov=c) for m, c in zip(means, covariance)])
    sklearn_logpdf = _estimate_log_gaussian_prob(points, means, _compute_precision_cholesky(covariance, "full"), "full")
    np.testing.assert_allclose(logpdf, sklearn_logpdf, atol=1e-12, rtol=0)
    weighted = logpdf + np.log(weights)
    posterior = np.exp(weighted - logsumexp(weighted, axis=1)[:, None])
    data = np.array([
        [-3, -0.8], [-2.8, -0.2], [-2.5, 0.5], [-2.1, -0.5], [-1.7, 0.7], [-1.2, 0.1],
        [-0.4, -0.3], [0, 0], [0.4, 0.3], [1.2, -0.1], [1.7, -0.7], [2.1, 0.5],
        [2.5, -0.5], [2.8, 0.2], [3, 0.8], [3.2, 0.1],
    ], dtype=np.float64)
    fits = []
    for k in (1, 2, 3):
        initial_means = np.array([[0, 0]] if k == 1 else [[-2, 0], [2, 0]] if k == 2 else [[-2, 0], [0, 0], [2, 0]], dtype=np.float64)
        initial_covariances = np.repeat(np.eye(2)[None, :, :], k, axis=0)
        model = GaussianMixture(n_components=k, covariance_type="full", tol=1e-3, reg_covar=1e-6,
                                max_iter=100, n_init=1, init_params="random", random_state=SEED,
                                weights_init=np.full(k, 1 / k), means_init=initial_means,
                                precisions_init=np.linalg.inv(initial_covariances)).fit(data)
        if not model.converged_:
            raise ValueError(f"Fixture EM did not converge for K={k}")
        likelihood = model.score(data) * len(data)
        parameters = (k - 1) + k * 2 + k * 2 * 3 / 2
        bic = -2 * likelihood + parameters * np.log(len(data))
        np.testing.assert_allclose(model.bic(data), bic, atol=1e-12, rtol=0)
        fits.append(dict(components=k, initialWeights=[1 / k] * k, initialMeans=initial_means.tolist(),
                         initialCovariances=initial_covariances.tolist(), converged=bool(model.converged_),
                         iterations=int(model.n_iter_), weights=model.weights_.tolist(), means=model.means_.tolist(),
                         covariances=model.covariances_.tolist(), responsibilities=model.predict_proba(data).tolist(),
                         totalLogLikelihood=float(likelihood), bic=float(bic)))
    # Independent closed-form anchor: diag(4,9), x=mean=(0,0): L=diag(2,3), det=36.
    anchor = dict(point=[0, 0], mean=[0, 0], covariance=[[4, 0], [0, 9]], cholesky=[[2, 0], [0, 3]],
                  logDeterminant=float(np.log(36)), logPdf=float(-np.log(2 * np.pi) - np.log(6)))
    return dict(recipe=RECIPE, seed=SEED, dtype="float64", points=points.tolist(), means=means.tolist(),
                covariances=covariance.tolist(), weights=weights.tolist(), logPdf=logpdf.tolist(),
                cholesky=np.linalg.cholesky(covariance).tolist(), logDeterminants=np.linalg.slogdet(covariance)[1].tolist(),
                responsibilities=posterior.tolist(), threshold=0.1,
                memberships=[np.flatnonzero(p > 0.1).tolist() for p in posterior], analytic=anchor,
                emPoints=data.tolist(), fits=fits)


def fuzzy_fixture() -> dict:
    import numpy as np
    from scipy.spatial.distance import cdist
    from umap.umap_ import smooth_knn_dist, compute_membership_strengths, fuzzy_simplicial_set

    cases = []
    for name, values, k in (
        ("ties-and-duplicates", [[1, 0], [1, 0], [0, 1], [-1, 0], [0, -1], [1, 1]], 4),
        ("all-duplicates", [[1, 0]] * 4, 3),
        ("disconnected", [[1, 0], [1, 0.1], [1, -0.1], [-1, 0], [-1, 0.1], [-1, -0.1]], 3),
    ):
        points = np.array(values, dtype=np.float64)
        distances = cdist(points, points, metric="cosine")
        # Self is ALWAYS first, followed by distance then original index, even with duplicate vectors.
        indices = np.array([[i] + sorted((j for j in range(len(points)) if j != i), key=lambda j: (distances[i, j], j))[:k-1]
                            for i in range(len(points))], dtype=np.int32)
        knn_distances = np.array([[distances[i, j] if i != j else 0 for j in row] for i, row in enumerate(indices)], dtype=np.float32)
        sigmas, rhos = smooth_knn_dist(knn_distances, float(k), n_iter=64, local_connectivity=1.0, bandwidth=1.0)
        rows, columns, strengths, _ = compute_membership_strengths(indices, knn_distances, sigmas, rhos)
        directed = np.zeros((len(points), len(points)), dtype=np.float32)
        directed[rows, columns] = strengths
        # Actual official graph entry point, using explicit independent cosine kNN rather than approximate search.
        graph, official_sigmas, official_rhos = fuzzy_simplicial_set(points.astype(np.float32), k, np.random.RandomState(SEED),
            "cosine", knn_indices=indices, knn_dists=knn_distances, set_op_mix_ratio=1.0, local_connectivity=1.0)
        union = directed + directed.T - directed * directed.T
        np.testing.assert_allclose(graph.toarray(), union, atol=1e-7, rtol=0)
        np.testing.assert_array_equal(official_sigmas, sigmas)
        np.testing.assert_array_equal(official_rhos, rhos)
        cases.append(dict(name=name, points=points.tolist(), neighbors=k, indices=indices.tolist(), distances=knn_distances.tolist(),
                          sigmas=sigmas.tolist(), rhos=rhos.tolist(), directed=directed.tolist(), symmetric=graph.toarray().tolist()))
    return dict(recipe=RECIPE, seed=SEED, dtype="float32 official calibration/weights; float64 independent cosine distances",
                selfNeighbor="column zero; excluded from smooth sum; diagonal membership zero", tolerance=1e-6, cases=cases)


def optimizer_fixture() -> dict:
    import numba
    import numpy as np
    from umap.layouts import _optimize_layout_euclidean_single_epoch
    from umap.umap_ import find_ab_params
    from umap.utils import tau_rand_int

    official_epoch = numba.njit(_optimize_layout_euclidean_single_epoch, fastmath=True)
    fitted_a, fitted_b = find_ab_params(1.0, 0.1)
    cases = []
    for name in ("analytic-attraction", "attraction-and-negative-sampling"):
        analytic = name == "analytic-attraction"
        embedding = np.array([[0, 0], [1, 0]] if analytic else [[0.25, -0.5], [1.25, 0.75], [-0.75, 1.5], [2, -1]], dtype=np.float32)
        initial = embedding.copy()
        head = np.array([0] if analytic else [0, 1], dtype=np.int32)
        tail = np.array([1] if analytic else [1, 2], dtype=np.int32)
        intervals = np.ones(len(head), dtype=np.float64)
        next_sample = np.ones(len(head), dtype=np.float64)
        negative_intervals = intervals / 5
        next_negative = np.ones(len(head), dtype=np.float64) if analytic else negative_intervals.copy()
        negative_initial = next_negative.copy()
        rng = np.array([42, 12345, 98765], dtype=np.int64)
        rng_initial = rng.copy()
        sample_rng = rng.copy()
        negative_tape = [[int(tau_rand_int(sample_rng) % len(embedding)) for _ in range(int((1 - t) / interval))]
                         for t, interval in zip(next_negative, negative_intervals)]
        a, b = (1.0, 1.0) if analytic else (float(fitted_a), float(fitted_b))
        # densMAP disabled; zero scratch arrays satisfy the official signature without any density correction.
        zero = np.zeros(len(embedding), dtype=np.float32)
        official_epoch(embedding, embedding, head, tail, len(embedding), intervals, a, b, rng, 1.0, 2, True,
                       0.25, negative_intervals, next_negative, next_sample, 1, False, zero, zero, 0.0, 1.0, 0.0,
                       0.0, zero, zero, 0.0)
        if analytic:
            np.testing.assert_array_equal(embedding, [[0.25, 0], [0.75, 0]])
        np.testing.assert_array_equal(rng, sample_rng)
        cases.append(dict(name=name, initial=initial.tolist(), head=head.tolist(), tail=tail.tolist(), epoch=1,
                          epochsPerSample=intervals.tolist(), initialNextSample=[1.0] * len(head),
                          epochsPerNegativeSample=negative_intervals.tolist(), initialNextNegative=negative_initial.tolist(),
                          nextSample=next_sample.tolist(), nextNegative=next_negative.tolist(), initialRng=rng_initial.tolist(),
                          finalRng=rng.tolist(), negativeSamples=negative_tape, a=a, b=b, alpha=0.25, gamma=1.0,
                          moveOther=True, expected=embedding.tolist(), delta=(embedding - initial).tolist()))
    return dict(recipe=RECIPE, seed=SEED, dtype="float32 coordinates/rdist; float64 scheduling", tolerance=1e-6,
                minDist=0.1, spread=1.0, fittedA=float(fitted_a), fittedB=float(fitted_b), negativeSampleRate=5,
                gradientClip=4.0, repulsionDistanceFloor=0.001, cases=cases)


def generate(directory: Path, output: Path) -> None:
    if platform.python_version() != "3.11.16":
        raise ValueError("Reference recipe requires Python 3.11.16 (no implicit version substitution)")
    packages = locked_packages()
    artifacts = json.loads((directory / "artifacts.json").read_text())["artifacts"]
    if {a["name"] for a in artifacts} != set(packages):
        raise ValueError("Reference artifact closure differs from lock")
    for artifact in artifacts:
        version, hashes = packages[artifact["name"]]
        if artifact["version"] != version or importlib.metadata.version(artifact["name"]) != version:
            raise ValueError(f"Installed dependency mismatch: {artifact['name']}")
        data = (directory / artifact["filename"]).read_bytes()
        if artifact["sha256"] not in hashes or sha256(data) != artifact["sha256"]:
            raise ValueError(f"Missing/corrupt artifact: {artifact['name']}")
    if sha256((directory / "cluster_utils.py").read_bytes()) != REFERENCE_SHA256:
        raise ValueError("Missing/corrupt official RAPTOR commit source")
    output.mkdir(parents=True, exist_ok=True)
    for name, fixture in (("gaussian.json", gaussian_fixture()), ("fuzzy-graph.json", fuzzy_fixture()),
                          ("optimizer-step.json", optimizer_fixture())):
        write_json(output / name, fixture)
    import sklearn.mixture._gaussian_mixture as gaussian_module
    import umap.layouts as layout_module
    import umap.umap_ as umap_module
    kernels = [dict(name=name, sha256=sha256(Path(module.__file__).read_bytes())) for name, module in
               (("sklearn/mixture/_gaussian_mixture.py", gaussian_module), ("umap/layouts.py", layout_module), ("umap/umap_.py", umap_module))]
    manifest = dict(recipe=RECIPE, raptorCommit=COMMIT, pythonVersion=platform.python_version(),
                    platform=f"{platform.system()}-{platform.machine()}", seed=SEED,
                    referenceSource=dict(name="raptor/cluster_utils.py", url=REFERENCE_URL, sha256=REFERENCE_SHA256),
                    lockfileSha256=sha256((HERE / "requirements.lock").read_bytes()), generatorSha256=sha256(Path(__file__).read_bytes()),
                    artifacts=artifacts, kernels=kernels,
                    fixtures={name: sha256((output / name).read_bytes()) for name in ("gaussian.json", "fuzzy-graph.json", "optimizer-step.json")},
                    limitations=["Official RAPTOR leaves sklearn unpinned and UMAP unseeded; this is a separately pinned kernel oracle, not an upstream end-to-end reproduction.",
                                 "Official UMAP calibration and optimizer use float32 and Tausworthe sampling; managed production uses double and SplitMix64. Exported negative-sample tapes isolate the optimizer equations from PRNG differences.",
                                 "Same-platform reference regeneration is checked byte-for-byte; Python layouts and cross-architecture bit identity are not acceptance contracts."])
    write_json(output / "manifest.json", manifest)
    print(f"Wrote 4 independent reference fixtures to {output}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path, required=True, help="Isolated downloaded source/package directory")
    parser.add_argument("--prepare", action="store_true", help="Fetch and verify packages/source (network required)")
    parser.add_argument("--output", type=Path, help="Write numeric JSON fixtures; no network access")
    args = parser.parse_args()
    if args.prepare:
        prepare(args.artifacts)
    if args.output:
        generate(args.artifacts, args.output)
    if not args.prepare and not args.output:
        parser.error("Specify --prepare or --output")


if __name__ == "__main__":
    main()
