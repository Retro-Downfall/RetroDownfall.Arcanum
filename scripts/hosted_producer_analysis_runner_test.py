"""Contracts for the bounded hosted-producer analysis runner."""

import ctypes
import importlib.util
import io
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[1]

SPEC = importlib.util.spec_from_file_location(
    "hosted_producer_analysis_runner",
    ROOT / "scripts/hosted_producer_analysis_runner.py",
)

assert SPEC is not None

assert SPEC.loader is not None

RUNNER = importlib.util.module_from_spec(SPEC)

SPEC.loader.exec_module(RUNNER)


class ScheduledProcesses:
    """Replace external dotnet processes, retaining real shard/TRX handling."""

    def __init__(self, *, failure=None, missing=None, finish_time=None):
        self.events = []
        self.environments = []
        self.active_slots = 0
        self.peak_slots = 0
        self.failure = failure
        self.missing = missing
        self.finish_time = finish_time
        self.clock = 100.0
        self.wait_timeouts = []

    def __call__(self, command, **kwargs):
        settings = Path(command[command.index("--settings") + 1])
        selected = RUNNER.ElementTree.parse(settings).findtext(
            "./RunConfiguration/TestCaseFilter"
        )
        names = [item.split("=", 1)[1] for item in selected.split("|")]
        label = names[0]
        environment = kwargs["env"]
        slots = int(environment.get("ARCANUM_HOSTED_ANALYSIS_ROOT_WORKERS", "1"))
        self.events.append(("start", label))
        self.environments.append(environment)
        self.active_slots += slots
        self.peak_slots = max(self.peak_slots, self.active_slots)
        trx_name = command[command.index("--logger") + 1].split("=", 1)[1]
        results = Path(command[command.index("--results-directory") + 1])
        process = mock.Mock(pid=100 + len(self.environments), stdout=io.StringIO(""))

        def wait(timeout):
            self.wait_timeouts.append(timeout)
            self.events.append(("finish", label))
            self.active_slots -= slots
            if self.finish_time is not None:
                self.clock = self.finish_time
            if label != self.missing:
                root = RUNNER.ElementTree.Element("TestRun")
                entries = RUNNER.ElementTree.SubElement(root, "Results")
                for name in names:
                    RUNNER.ElementTree.SubElement(
                        entries, "UnitTestResult", testName=name,
                        outcome="Failed" if label == self.failure else "Passed",
                    )
                RUNNER.ElementTree.ElementTree(root).write(results / trx_name)
            return 17 if label == self.failure else 0

        process.wait.side_effect = wait
        return process


class HostedProducerAnalysisRunnerTests(unittest.TestCase):
    def test_plan_reserves_two_of_four_slots_for_production(self):
        shards = RUNNER.plan_shards(
            [RUNNER.DiscoveredMethod("Tests.Production", 1)],
            [RUNNER.DiscoveredMethod(f"Tests.Fixture{index}", 1) for index in range(4)],
            4,
        )
        self.assertEqual([1, 2, 2], [shard.case_count for shard in shards])

    def test_production_environment_overrides_inherited_worker_setting(self):
        with mock.patch.dict(os.environ, {"ARCANUM_HOSTED_ANALYSIS_ROOT_WORKERS": "invalid"}):
            environment = RUNNER.shard_environment(Path("results"), True)
        self.assertEqual("1", environment.get("ARCANUM_HOSTED_ANALYSIS_ROOT_WORKERS"))

    def test_jobs_one_two_four_bound_production_and_fixture_capacity(self):
        production = [RUNNER.DiscoveredMethod("Tests.Production", 1)]
        fixtures = [
            RUNNER.DiscoveredMethod("Tests.FixtureA", 1),
            RUNNER.DiscoveredMethod("Tests.FixtureB", 1),
        ]
        cases = [
            (1, [3], 1, [("start", "Tests.Production"), ("finish", "Tests.Production")]),
            (2, [1, 2], 2, [
                ("start", "Tests.Production"), ("finish", "Tests.Production"),
                ("start", "Tests.FixtureA"), ("finish", "Tests.FixtureA"),
            ]),
            (4, [1, 1, 1], 4, [
                ("start", "Tests.Production"), ("start", "Tests.FixtureA"),
                ("start", "Tests.FixtureB"), ("finish", "Tests.Production"),
                ("finish", "Tests.FixtureA"), ("finish", "Tests.FixtureB"),
            ]),
        ]
        for jobs, counts, peak, events in cases:
            with self.subTest(jobs=jobs):
                shards = RUNNER.plan_shards(production, fixtures, jobs)
                self.assertEqual(counts, [shard.case_count for shard in shards])
                children = ScheduledProcesses()
                with tempfile.TemporaryDirectory() as temp, mock.patch.object(
                    RUNNER.subprocess, "Popen", side_effect=children
                ), mock.patch.object(RUNNER, "attach_process_tree"), mock.patch.object(
                    RUNNER, "release_process_tree"
                ), mock.patch.dict(os.environ, {"ARCANUM_HOSTED_ANALYSIS_ROOT_WORKERS": "invalid"}):
                    result = RUNNER.run_shards(
                        "dotnet", Path("tests.csproj"), Path(temp), shards,
                        production_shard_index=0, worker_count=jobs,
                    )
                self.assertEqual(0, RUNNER.require_complete_success(result))
                self.assertEqual(3, result.completed_count)
                self.assertEqual(events, children.events)
                self.assertEqual(peak, children.peak_slots)
                self.assertEqual(
                    "1" if jobs == 1 else "2",
                    children.environments[0]["ARCANUM_HOSTED_ANALYSIS_ROOT_WORKERS"],
                )
                for environment in children.environments[1:]:
                    self.assertNotIn("ARCANUM_HOSTED_ANALYSIS_ROOT_WORKERS", environment)
                    self.assertNotIn("ARCANUM_HOSTED_ANALYSIS_PROGRESS", environment)

    def test_queued_fixture_results_remain_required(self):
        shards = RUNNER.plan_shards(
            [RUNNER.DiscoveredMethod("Tests.Production", 1)],
            [RUNNER.DiscoveredMethod("Tests.Fixture", 1)], 2,
        )
        for missing in (True, False):
            with self.subTest(missing=missing):
                children = ScheduledProcesses(
                    missing="Tests.Fixture" if missing else None,
                    failure=None if missing else "Tests.Fixture",
                )
                with tempfile.TemporaryDirectory() as temp, mock.patch.object(
                    RUNNER.subprocess, "Popen", side_effect=children
                ), mock.patch.object(RUNNER, "attach_process_tree"), mock.patch.object(
                    RUNNER, "release_process_tree"
                ):
                    def run():
                        return RUNNER.run_shards(
                            "dotnet", Path("tests.csproj"), Path(temp), shards,
                            production_shard_index=0, worker_count=2,
                        )
                    if missing:
                        with self.assertRaisesRegex(RuntimeError, "shard 2 produced no TRX"):
                            run()
                    else:
                        result = run()
                        self.assertEqual((0, 17), result.exit_codes)
                        self.assertEqual(17, RUNNER.require_complete_success(result))

    def test_queued_work_uses_the_original_absolute_deadline(self):
        shards = RUNNER.plan_shards(
            [RUNNER.DiscoveredMethod("Tests.Production", 1)],
            [RUNNER.DiscoveredMethod("Tests.Fixture", 1)], 2,
        )
        for finish_time, expected_starts in ((109.0, 2), (111.0, 1)):
            with self.subTest(finish_time=finish_time):
                children = ScheduledProcesses(finish_time=finish_time)
                with tempfile.TemporaryDirectory() as temp, mock.patch.object(
                    RUNNER.subprocess, "Popen", side_effect=children
                ), mock.patch.object(RUNNER, "attach_process_tree"), mock.patch.object(
                    RUNNER, "release_process_tree"
                ), mock.patch.object(RUNNER, "terminate_processes") as terminate, mock.patch.object(
                    RUNNER.time, "monotonic", side_effect=lambda: children.clock
                ):
                    def run():
                        return RUNNER.run_shards(
                            "dotnet", Path("tests.csproj"), Path(temp), shards,
                            production_shard_index=0, worker_count=2,
                            timeout_seconds=20, deadline=110.0,
                        )
                    if finish_time > 110:
                        with self.assertRaisesRegex(RuntimeError, "deadline"):
                            run()
                        self.assertEqual(1, len(terminate.call_args.args[0]))
                    else:
                        self.assertEqual(0, RUNNER.require_complete_success(run()))
                        self.assertEqual([10.0, 1.0], children.wait_timeouts)
                self.assertEqual(expected_starts, len(children.environments))

    def test_discovery_groups_theory_cases_by_fully_qualified_method(self):
        output = """Test run
The following Tests are available:
    Example.Tests.InventoryTests.Fact
    Example.Tests.InventoryTests.Theory(value: 1)
    Example.Tests.InventoryTests.Theory(value: 2)
    Example.Tests.OtherTests.Check
"""

        methods = RUNNER.discover_methods(output)

        self.assertEqual(
            [
                RUNNER.DiscoveredMethod("Example.Tests.InventoryTests.Fact", 1),
                RUNNER.DiscoveredMethod("Example.Tests.InventoryTests.Theory", 2),
                RUNNER.DiscoveredMethod("Example.Tests.OtherTests.Check", 1),
            ],
            methods,
        )

    def test_discovery_rejects_an_empty_selection(self):
        with self.assertRaisesRegex(ValueError, "no hosted-producer analysis tests"):
            RUNNER.discover_methods("The following Tests are available:\n")

    def test_shards_are_balanced_without_splitting_theory_methods(self):
        methods = [
            RUNNER.DiscoveredMethod("Tests.Heavy", 5),
            RUNNER.DiscoveredMethod("Tests.Medium", 3),
            RUNNER.DiscoveredMethod("Tests.LightA", 1),
            RUNNER.DiscoveredMethod("Tests.LightB", 1),
        ]

        shards = RUNNER.balance_methods(methods, 2)

        self.assertEqual([5, 5], [shard.case_count for shard in shards])

        selected = [method.name for shard in shards for method in shard.methods]

        self.assertCountEqual([method.name for method in methods], selected)

        self.assertEqual(len(selected), len(set(selected)))

    def test_filter_selects_exact_fully_qualified_methods(self):
        shard = RUNNER.AnalysisShard(
            index=0,
            methods=(
                RUNNER.DiscoveredMethod("Example.Tests.First", 1),
                RUNNER.DiscoveredMethod("Example.Tests.Second", 2),
            ),
            case_count=3,
        )

        self.assertEqual(
            "FullyQualifiedName=Example.Tests.First|FullyQualifiedName=Example.Tests.Second",
            RUNNER.shard_filter(shard),
        )

    def test_shard_runsettings_keep_long_filters_off_the_command_line(self):
        methods = tuple(
            RUNNER.DiscoveredMethod(
                f"Example.Tests.InventoryTests.Proof{index:04d}"
                + ("X" * 64),
                1,
            )
            for index in range(500)
        )

        shard = RUNNER.AnalysisShard(
            index=2,
            methods=methods,
            case_count=len(methods),
        )

        expected_filter = RUNNER.shard_filter(shard)

        self.assertGreater(len(expected_filter), 32767)

        with tempfile.TemporaryDirectory() as temp:
            path = RUNNER.write_shard_runsettings(Path(temp), shard)

            root = RUNNER.ElementTree.parse(path).getroot()

        self.assertEqual(
            expected_filter,
            root.findtext("./RunConfiguration/TestCaseFilter"),
        )

        self.assertEqual(
            "true",
            root.findtext("./RunConfiguration/TreatNoTestsAsError"),
        )

    def test_plan_keeps_the_production_graph_in_one_lane(self):
        production = [
            RUNNER.DiscoveredMethod("Tests.ProductionGraph", 3),
            RUNNER.DiscoveredMethod("Tests.ProductionManifest", 1),
        ]

        fixtures = [
            RUNNER.DiscoveredMethod("Tests.FixtureA", 4),
            RUNNER.DiscoveredMethod("Tests.FixtureB", 3),
            RUNNER.DiscoveredMethod("Tests.FixtureC", 2),
            RUNNER.DiscoveredMethod("Tests.FixtureD", 1),
        ]

        shards = RUNNER.plan_shards(production, fixtures, 3)

        self.assertEqual(
            ["Tests.ProductionGraph", "Tests.ProductionManifest"],
            [method.name for method in shards[0].methods],
        )

        self.assertEqual(4, shards[0].case_count)

        fixture_names = [
            method.name
            for shard in shards[1:]
            for method in shard.methods
        ]

        self.assertCountEqual(
            [method.name for method in fixtures],
            fixture_names,
        )

        self.assertFalse(
            set(method.name for method in production).intersection(fixture_names)
        )

    def test_single_worker_combines_production_and_fixtures_once(self):
        production = [RUNNER.DiscoveredMethod("Tests.Production", 2)]

        fixtures = [RUNNER.DiscoveredMethod("Tests.Fixture", 3)]

        shards = RUNNER.plan_shards(production, fixtures, 1)

        self.assertEqual(1, len(shards))

        self.assertEqual(5, shards[0].case_count)

        self.assertCountEqual(
            ["Tests.Production", "Tests.Fixture"],
            [method.name for method in shards[0].methods],
        )

    def test_plan_rejects_a_method_selected_by_both_lanes(self):
        duplicate = RUNNER.DiscoveredMethod("Tests.Duplicate", 1)

        with self.assertRaisesRegex(ValueError, "both production and fixture"):
            RUNNER.plan_shards([duplicate], [duplicate], 2)

    def test_main_discovers_production_and_fixture_lanes_separately(self):
        production = [RUNNER.DiscoveredMethod("Tests.Production", 2)]

        fixtures = [RUNNER.DiscoveredMethod("Tests.Fixture", 3)]

        with tempfile.TemporaryDirectory() as temp:
            result = RUNNER.AnalysisRunResult(
                exit_codes=(0, 0),
                completed_count=5,
                passed_count=5,
                expected_count=5,
                log_path=Path(temp) / "analysis.log",
            )

            with mock.patch.object(
                RUNNER,
                "_discover",
                side_effect=[production, fixtures],
            ) as discover, mock.patch.object(
                RUNNER,
                "run_shards",
                return_value=result,
            ) as run, mock.patch.object(
                RUNNER.time, "monotonic", side_effect=[100.0, 101.0, 108.0, 112.0]
            ):
                exit_code = RUNNER.main(
                    [
                        "--project",
                        str(Path(temp) / "tests.csproj"),
                        "--results-directory",
                        temp,
                        "--jobs",
                        "2",
                        "--configuration",
                        "Release",
                        "--timeout-seconds",
                        "20",
                    ]
                )

        self.assertEqual(0, exit_code)

        self.assertEqual(
            [
                mock.call(
                    "dotnet",
                    mock.ANY,
                    RUNNER.PRODUCTION_FILTER,
                    "Release",
                    mock.ANY,
                ),
                mock.call(
                    "dotnet",
                    mock.ANY,
                    RUNNER.FIXTURE_FILTER,
                    "Release",
                    mock.ANY,
                ),
            ],
            discover.call_args_list,
        )

        planned = run.call_args.args[3]

        self.assertEqual(["Tests.Production"], [item.name for item in planned[0].methods])

        self.assertEqual("Release", run.call_args.kwargs["configuration"])

        self.assertEqual(2, run.call_args.kwargs.get("worker_count"))

        self.assertEqual(120.0, run.call_args.kwargs["deadline"])

        self.assertEqual(8.0, run.call_args.kwargs["timeout_seconds"])

        self.assertEqual([19.0, 12.0], [call.args[4] for call in discover.call_args_list])

    def test_main_accepts_a_thirty_minute_deadline(self):
        production = [RUNNER.DiscoveredMethod("Tests.Production", 1)]

        with tempfile.TemporaryDirectory() as temp:
            result = RUNNER.AnalysisRunResult(
                exit_codes=(0,),
                completed_count=1,
                passed_count=1,
                expected_count=1,
                log_path=Path(temp) / "analysis.log",
            )

            with mock.patch.object(
                RUNNER,
                "_discover",
                side_effect=[production, []],
            ) as discover, mock.patch.object(
                RUNNER,
                "run_shards",
                return_value=result,
            ) as run:
                exit_code = RUNNER.main(
                    [
                        "--project",
                        str(Path(temp) / "tests.csproj"),
                        "--results-directory",
                        temp,
                        "--timeout-seconds",
                        "1800",
                    ]
                )

        self.assertEqual(0, exit_code)

        self.assertEqual(2, discover.call_count)

        run.assert_called_once()

    def test_main_rejects_a_deadline_above_thirty_minutes(self):
        with tempfile.TemporaryDirectory() as temp, mock.patch.object(
            RUNNER,
            "_discover",
        ) as discover:
            exit_code = RUNNER.main(
                [
                    "--project",
                    str(Path(temp) / "tests.csproj"),
                    "--results-directory",
                    temp,
                    "--timeout-seconds",
                    "1801",
                ]
            )

        self.assertEqual(2, exit_code)

        discover.assert_not_called()

    def test_main_rejects_non_numeric_environment_limits(self):
        with tempfile.TemporaryDirectory() as temp, mock.patch.dict(
            os.environ,
            {
                "ARCANUM_ANALYSIS_JOBS": "many",
                "ARCANUM_ANALYSIS_TIMEOUT_SECONDS": "later",
            },
        ), mock.patch.object(RUNNER, "_discover") as discover:
            exit_code = RUNNER.main(
                [
                    "--project",
                    str(Path(temp) / "tests.csproj"),
                    "--results-directory",
                    temp,
                ]
            )

        self.assertEqual(2, exit_code)

        discover.assert_not_called()

    def test_discovery_timeout_fails_closed(self):
        process = mock.Mock(pid=123)

        process.communicate.side_effect = RUNNER.subprocess.TimeoutExpired(
            "dotnet",
            3,
        )

        with mock.patch.object(
            RUNNER.subprocess,
            "Popen",
            return_value=process,
        ) as popen, mock.patch.object(
            RUNNER,
            "attach_process_tree",
        ), mock.patch.object(
            RUNNER,
            "release_process_tree",
        ), mock.patch.object(
            RUNNER,
            "terminate_processes",
        ) as terminate, mock.patch.object(
            RUNNER.subprocess,
            "run",
            side_effect=AssertionError("discovery must own its process"),
        ):
            with self.assertRaisesRegex(RuntimeError, "discovery exceeded"):
                RUNNER._discover(
                    "dotnet",
                    Path("tests.csproj"),
                    RUNNER.PRODUCTION_FILTER,
                    None,
                    3,
                )

        terminate.assert_called_once_with([process])

        self.assertEqual(
            os.name == "posix",
            popen.call_args.kwargs["start_new_session"],
        )

    def test_discovery_interruption_cleans_up_its_process_tree(self):
        process = mock.Mock(pid=124)

        process.communicate.side_effect = KeyboardInterrupt()

        with mock.patch.object(
            RUNNER.subprocess,
            "Popen",
            return_value=process,
        ), mock.patch.object(
            RUNNER,
            "attach_process_tree",
        ), mock.patch.object(
            RUNNER,
            "release_process_tree",
        ), mock.patch.object(
            RUNNER,
            "terminate_processes",
        ) as terminate:
            with self.assertRaises(KeyboardInterrupt):
                RUNNER._discover(
                    "dotnet",
                    Path("tests.csproj"),
                    RUNNER.PRODUCTION_FILTER,
                    None,
                    3,
                )

        terminate.assert_called_once_with([process])

    def test_windows_discovery_is_suspended_until_its_job_is_owned(self):
        process = mock.Mock(pid=125)

        project = Path("tests.csproj")

        process.communicate.return_value = (
            "The following Tests are available:\n    Tests.Proof\n",
            None,
        )

        process.returncode = 0

        process.poll.return_value = 0

        events = []

        with mock.patch.object(
            RUNNER.os,
            "name",
            "nt",
        ), mock.patch.object(
            RUNNER.subprocess,
            "Popen",
            return_value=process,
        ) as popen, mock.patch.object(
            RUNNER,
            "attach_process_tree",
            side_effect=lambda owned: events.append(("attach", owned.pid)),
        ), mock.patch.object(
            RUNNER,
            "release_process_tree",
            side_effect=lambda owned: events.append(("release", owned.pid)),
        ):
            methods = RUNNER._discover(
                "dotnet",
                project,
                RUNNER.PRODUCTION_FILTER,
                None,
                3,
            )

        self.assertEqual([RUNNER.DiscoveredMethod("Tests.Proof", 1)], methods)

        self.assertEqual(
            RUNNER.WINDOWS_CREATE_SUSPENDED,
            popen.call_args.kwargs["creationflags"],
        )

        self.assertEqual([("attach", 125), ("release", 125)], events)

    @unittest.skipUnless(os.name == "posix", "POSIX process groups only")
    def test_cleanup_terminates_the_whole_posix_process_group(self):
        process = mock.Mock(pid=123)

        process.poll.return_value = None

        process.wait.return_value = 0

        with mock.patch.object(RUNNER.os, "killpg") as kill_group:
            RUNNER.terminate_processes([process])

        self.assertEqual(
            [
                mock.call(123, RUNNER.signal.SIGTERM),
                mock.call(123, RUNNER.signal.SIGKILL),
            ],
            kill_group.call_args_list,
        )

        process.terminate.assert_not_called()

    @unittest.skipUnless(os.name == "posix", "POSIX process groups only")
    def test_cleanup_reaps_descendants_after_the_parent_has_already_exited(self):
        process = mock.Mock(pid=789)

        process.poll.return_value = 0

        with mock.patch.object(RUNNER.os, "killpg") as kill_group:
            RUNNER.terminate_processes([process])

        self.assertEqual(
            [
                mock.call(789, RUNNER.signal.SIGTERM),
                mock.call(789, RUNNER.signal.SIGKILL),
            ],
            kill_group.call_args_list,
        )

    @unittest.skipUnless(os.name == "posix", "POSIX process groups only")
    def test_cleanup_denied_group_signal_does_not_skip_later_processes(self):
        denied = mock.Mock(pid=123)

        denied.poll.return_value = 0

        other = mock.Mock(pid=456)

        other.poll.return_value = 0

        def signal_group(pid, signal):
            if pid == denied.pid:
                raise PermissionError(1, "Operation not permitted")

        with mock.patch.object(
            RUNNER.os, "killpg", side_effect=signal_group
        ) as kill_group, mock.patch.object(RUNNER.sys, "stderr", io.StringIO()) as errors:
            RUNNER.terminate_processes([denied, other])

        self.assertIn(mock.call(456, RUNNER.signal.SIGKILL), kill_group.call_args_list)

        self.assertIn("123", errors.getvalue())

        self.assertIn("Operation not permitted", errors.getvalue())

    @unittest.skipUnless(os.name == "posix", "POSIX process groups only")
    def test_cleanup_denied_parent_fallback_does_not_skip_later_processes(self):
        denied = mock.Mock(pid=123)

        denied.poll.return_value = None

        denied.terminate.side_effect = PermissionError(1, "Operation not permitted")

        denied.kill.side_effect = PermissionError(1, "Operation not permitted")

        other = mock.Mock(pid=456)

        other.poll.return_value = 0

        def signal_group(pid, signal):
            if pid == denied.pid:
                raise PermissionError(1, "Operation not permitted")

        with mock.patch.object(
            RUNNER.os, "killpg", side_effect=signal_group
        ) as kill_group, mock.patch.object(RUNNER.sys, "stderr", io.StringIO()) as errors:
            RUNNER.terminate_processes([denied, other])

        denied.terminate.assert_called_once_with()

        denied.kill.assert_called_once_with()

        self.assertIn(mock.call(456, RUNNER.signal.SIGKILL), kill_group.call_args_list)

        self.assertIn("Operation not permitted", errors.getvalue())

    def test_shard_deadline_survives_cleanup_error(self):
        process = mock.Mock(pid=123)

        process.stdout = io.StringIO("")

        process.wait.side_effect = subprocess.TimeoutExpired("dotnet", 1)

        shard = RUNNER.AnalysisShard(0, (RUNNER.DiscoveredMethod("Tests.Proof", 1),), 1)

        with tempfile.TemporaryDirectory() as temp, mock.patch.object(
            RUNNER.subprocess, "Popen", return_value=process
        ), mock.patch.object(RUNNER, "attach_process_tree"), mock.patch.object(
            RUNNER, "release_process_tree"
        ), mock.patch.object(
            RUNNER, "terminate_processes", side_effect=PermissionError(1, "cleanup denied")
        ), mock.patch.object(RUNNER.sys, "stderr", io.StringIO()) as errors:
            with self.assertRaisesRegex(RuntimeError, "exceeded its 1-second deadline"):
                RUNNER.run_shards(
                    "dotnet", Path("tests.csproj"), Path(temp), [shard], timeout_seconds=1
                )

        self.assertIn("cleanup denied", errors.getvalue())

    def test_shard_deadline_survives_job_release_error(self):
        process = mock.Mock(pid=123)

        process.stdout = io.StringIO("")

        process.wait.side_effect = subprocess.TimeoutExpired("dotnet", 1)

        shard = RUNNER.AnalysisShard(0, (RUNNER.DiscoveredMethod("Tests.Proof", 1),), 1)

        with tempfile.TemporaryDirectory() as temp, mock.patch.object(
            RUNNER.subprocess, "Popen", return_value=process
        ), mock.patch.object(RUNNER, "attach_process_tree"), mock.patch.object(
            RUNNER, "release_process_tree", side_effect=PermissionError(1, "release denied")
        ), mock.patch.object(RUNNER, "terminate_processes"), mock.patch.object(
            RUNNER.sys, "stderr", io.StringIO()
        ) as errors:
            with self.assertRaisesRegex(RuntimeError, "exceeded its 1-second deadline"):
                RUNNER.run_shards(
                    "dotnet", Path("tests.csproj"), Path(temp), [shard], timeout_seconds=1
                )

        self.assertIn("release denied", errors.getvalue())

    def test_windows_cleanup_terminates_the_dotnet_process_tree(self):
        process = mock.Mock(pid=456)

        process.poll.return_value = None

        process.wait.return_value = 0

        with mock.patch.object(
            RUNNER.os,
            "name",
            "nt",
        ), mock.patch.object(RUNNER.subprocess, "run") as run:
            RUNNER.terminate_processes([process])

        run.assert_called_once_with(
            ["taskkill", "/PID", "456", "/T", "/F"],
            stdout=RUNNER.subprocess.DEVNULL,
            stderr=RUNNER.subprocess.DEVNULL,
            check=False,
            timeout=3,
        )

    def test_windows_cleanup_targets_descendants_after_the_parent_exits(self):
        process = mock.Mock(pid=457)

        process.poll.return_value = 0

        with mock.patch.object(
            RUNNER.os,
            "name",
            "nt",
        ), mock.patch.object(
            RUNNER.subprocess,
            "run",
        ) as run, mock.patch.object(
            RUNNER,
            "release_process_tree",
        ) as release:
            RUNNER.terminate_processes([process])

        release.assert_called_once_with(process)

        run.assert_called_once_with(
            ["taskkill", "/PID", "457", "/T", "/F"],
            stdout=RUNNER.subprocess.DEVNULL,
            stderr=RUNNER.subprocess.DEVNULL,
            check=False,
            timeout=3,
        )

    def test_windows_cleanup_falls_back_when_taskkill_fails(self):
        process = mock.Mock(pid=458)

        process.poll.return_value = None

        process.wait.return_value = 0

        taskkill = mock.Mock(returncode=1)

        with mock.patch.object(
            RUNNER.os,
            "name",
            "nt",
        ), mock.patch.object(
            RUNNER.subprocess,
            "run",
            return_value=taskkill,
        ):
            RUNNER.terminate_processes([process])

        process.terminate.assert_called_once_with()

        process.kill.assert_called_once_with()

    def test_windows_job_handle_is_closed_after_the_parent_exits(self):
        process = mock.Mock(pid=459)

        with mock.patch.object(
            RUNNER.os,
            "name",
            "nt",
        ), mock.patch.dict(
            RUNNER._WINDOWS_JOB_HANDLES,
            {459: 77},
            clear=True,
        ), mock.patch.object(
            RUNNER,
            "_close_windows_job",
        ) as close:
            RUNNER.release_process_tree(process)

            self.assertNotIn(459, RUNNER._WINDOWS_JOB_HANDLES)

        close.assert_called_once_with(77)

    @unittest.skipUnless(os.name == "nt", "native Windows Job Object smoke")
    def test_windows_job_object_kills_child_after_parent_exit(self):
        from ctypes import wintypes

        parent = subprocess.Popen(
            [
                sys.executable,
                "-c",
                "import subprocess, sys, time; "
                "child = subprocess.Popen([sys.executable, '-c', "
                "'import time; time.sleep(60)'], "
                "stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL); "
                "print(child.pid, flush=True)",
            ],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            creationflags=RUNNER.process_creation_flags(),
        )

        try:
            RUNNER.attach_process_tree(parent)

            output, _ = parent.communicate(timeout=10)

            child_pid = int(output.strip())

            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

            kernel32.OpenProcess.argtypes = [
                wintypes.DWORD,
                wintypes.BOOL,
                wintypes.DWORD,
            ]

            kernel32.OpenProcess.restype = wintypes.HANDLE

            kernel32.WaitForSingleObject.argtypes = [
                wintypes.HANDLE,
                wintypes.DWORD,
            ]

            kernel32.WaitForSingleObject.restype = wintypes.DWORD

            kernel32.CloseHandle.argtypes = [wintypes.HANDLE]

            kernel32.CloseHandle.restype = wintypes.BOOL

            child = kernel32.OpenProcess(0x00100000, False, child_pid)

            self.assertTrue(child)

            try:
                self.assertEqual(
                    0x00000102,
                    kernel32.WaitForSingleObject(child, 0),
                )

                RUNNER.release_process_tree(parent)

                self.assertEqual(0, kernel32.WaitForSingleObject(child, 5000))
            finally:
                kernel32.CloseHandle(child)
        finally:
            RUNNER.release_process_tree(parent)

            if parent.poll() is None:
                parent.kill()

                parent.wait(timeout=3)

    def test_shard_environment_enables_root_progress_only_for_production(self):
        with tempfile.TemporaryDirectory() as temp, mock.patch.dict(
            os.environ,
            {
                "ARCANUM_HOSTED_ANALYSIS_PROGRESS": "stale.log",
                "ARBITRARY_SETTING": "preserved",
            },
            clear=True,
        ):
            production = RUNNER.shard_environment(Path(temp), True)

            fixture = RUNNER.shard_environment(Path(temp), False)

        self.assertEqual(
            str((Path(temp) / "hosted-producer-roots.log").resolve()),
            production["ARCANUM_HOSTED_ANALYSIS_PROGRESS"],
        )

        self.assertNotIn("ARCANUM_HOSTED_ANALYSIS_PROGRESS", fixture)

        self.assertEqual("preserved", production["ARBITRARY_SETTING"])

        self.assertEqual("preserved", fixture["ARBITRARY_SETTING"])

    def test_relative_root_progress_path_survives_child_working_directory(self):
        with tempfile.TemporaryDirectory() as temp:
            runner_directory = Path(temp) / "runner"

            child_directory = Path(temp) / "child"

            runner_directory.mkdir()

            child_directory.mkdir()

            previous_directory = Path.cwd()

            try:
                os.chdir(runner_directory)

                results_directory = Path("analysis-results")

                expected_path = (
                    runner_directory
                    / results_directory
                    / "hosted-producer-roots.log"
                )

                expected_path.parent.mkdir()

                expected_path.write_text("", encoding="utf-8")

                environment = RUNNER.shard_environment(
                    results_directory,
                    True,
                )

                child = subprocess.run(
                    [
                        sys.executable,
                        "-c",
                        (
                            "import os; from pathlib import Path; "
                            "path = Path(os.environ["
                            "'ARCANUM_HOSTED_ANALYSIS_PROGRESS']); "
                            "path.parent.mkdir(parents=True, exist_ok=True); "
                            "path.write_text('child progress\\n', "
                            "encoding='utf-8')"
                        ),
                    ],
                    cwd=child_directory,
                    env=environment,
                    check=False,
                )

                self.assertEqual(0, child.returncode)

                self.assertTrue(
                    Path(
                        environment["ARCANUM_HOSTED_ANALYSIS_PROGRESS"]
                    ).is_absolute()
                )

                self.assertEqual(
                    "child progress\n",
                    expected_path.read_text(encoding="utf-8"),
                )
            finally:
                os.chdir(previous_directory)

    def test_root_progress_reader_defers_partial_records(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "roots.log"

            path.write_text("first\npart", encoding="utf-8")

            lines, offset = RUNNER.read_progress_records(path, 0)

            self.assertEqual(["first"], lines)

            with path.open("a", encoding="utf-8") as stream:
                stream.write("ial\nsecond\n")

            remaining, final_offset = RUNNER.read_progress_records(path, offset)

        self.assertEqual(["partial", "second"], remaining)

        self.assertGreater(final_offset, offset)

    def test_worker_count_uses_available_cores_but_stays_bounded(self):
        with mock.patch.object(os, "cpu_count", return_value=18):
            self.assertEqual(16, RUNNER.default_worker_count())

        with mock.patch.object(os, "cpu_count", return_value=4):
            self.assertEqual(2, RUNNER.default_worker_count())

        with mock.patch.object(os, "cpu_count", return_value=1):
            self.assertEqual(1, RUNNER.default_worker_count())

    def test_result_parser_counts_only_terminal_test_lines(self):
        self.assertEqual(
            ("Passed", "Example.Tests.Passes"),
            RUNNER.parse_result_line("  Passed Example.Tests.Passes [12 ms]\n"),
        )

        self.assertEqual(
            ("Failed", "Example.Tests.Fails"),
            RUNNER.parse_result_line("  Failed Example.Tests.Fails [1 s]\n"),
        )

        self.assertEqual(
            ("Skipped", "Example.Tests.Skips"),
            RUNNER.parse_result_line("  Skipped Example.Tests.Skips [1 ms]\n"),
        )

        self.assertIsNone(
            RUNNER.parse_result_line("[xUnit.net 00:00:00.10] Starting test execution\n")
        )

    def test_trx_summary_uses_structured_outcomes(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "results.trx"

            path.write_text(
                """<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="Tests.Passes" outcome="Passed" />
    <UnitTestResult testName="Tests.Skips" outcome="NotExecuted" />
    <UnitTestResult testName="Tests.Fails" outcome="Failed" />
  </Results>
</TestRun>
""",
                encoding="utf-8",
            )

            summary = RUNNER.read_trx_summary(path)

        self.assertEqual(3, summary.completed_count)

        self.assertEqual(1, summary.passed_count)

        self.assertEqual(
            (("Tests.Skips", "NotExecuted"), ("Tests.Fails", "Failed")),
            summary.nonpassing,
        )

    def test_trx_summary_rejects_malformed_evidence(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "results.trx"

            path.write_text("<TestRun>", encoding="utf-8")

            with self.assertRaisesRegex(RuntimeError, "cannot parse analysis TRX"):
                RUNNER.read_trx_summary(path)

    def test_trx_summary_rejects_an_unselected_test_identity(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "results.trx"

            path.write_text(
                """<TestRun><Results>
<UnitTestResult testName="Tests.Unselected" outcome="Passed" />
</Results></TestRun>""",
                encoding="utf-8",
            )

            with self.assertRaisesRegex(RuntimeError, "unselected test"):
                RUNNER.read_trx_summary(
                    path,
                    [RUNNER.DiscoveredMethod("Tests.Selected", 1)],
                )

    def test_run_fails_closed_when_reported_completion_count_is_wrong(self):
        with tempfile.TemporaryDirectory() as temp:
            result = RUNNER.AnalysisRunResult(
                exit_codes=(0, 0),
                completed_count=2,
                passed_count=2,
                expected_count=3,
                log_path=Path(temp) / "analysis.log",
            )

            with self.assertRaisesRegex(RuntimeError, "reported 2 of 3"):
                RUNNER.require_complete_success(result)

    def test_run_preserves_the_first_nonzero_shard_exit_code(self):
        with tempfile.TemporaryDirectory() as temp:
            result = RUNNER.AnalysisRunResult(
                exit_codes=(0, 17, 9),
                completed_count=3,
                passed_count=2,
                expected_count=3,
                log_path=Path(temp) / "analysis.log",
            )

            self.assertEqual(17, RUNNER.require_complete_success(result))

    def test_run_rejects_skipped_mandatory_analysis(self):
        with tempfile.TemporaryDirectory() as temp:
            result = RUNNER.AnalysisRunResult(
                exit_codes=(0,),
                completed_count=3,
                passed_count=2,
                expected_count=3,
                log_path=Path(temp) / "analysis.log",
            )

            with self.assertRaisesRegex(RuntimeError, "only 2 passed"):
                RUNNER.require_complete_success(result)

    def test_run_shards_refuses_stale_trx_evidence(self):
        shard = RUNNER.AnalysisShard(
            index=0,
            methods=(RUNNER.DiscoveredMethod("Tests.Proof", 1),),
            case_count=1,
        )

        process = mock.Mock(pid=123, stdout=io.StringIO(""))

        process.wait.return_value = 0

        with tempfile.TemporaryDirectory() as temp:
            results = Path(temp)

            trx = results / "hosted-producer-analysis-shard-01.trx"

            trx.write_text(
                "<TestRun><Results>"
                '<UnitTestResult testName="Tests.Proof" outcome="Passed" />'
                "</Results></TestRun>",
                encoding="utf-8",
            )

            with mock.patch.object(
                RUNNER.subprocess,
                "Popen",
                return_value=process,
            ), mock.patch.object(
                RUNNER,
                "attach_process_tree",
            ), mock.patch.object(
                RUNNER,
                "release_process_tree",
            ):
                with self.assertRaisesRegex(RuntimeError, "produced no TRX"):
                    RUNNER.run_shards(
                        "dotnet",
                        Path("tests.csproj"),
                        results,
                        [shard],
                        timeout_seconds=1,
                    )


if __name__ == "__main__":
    unittest.main()
