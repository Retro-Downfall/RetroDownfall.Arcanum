"""Build the run-summary shape emitted by the VSTest TRX logger."""
import xml.etree.ElementTree as ET

NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"


def append_summary(root, outcomes):
    values = list(outcomes)
    passed = values.count("Passed")
    skipped = values.count("NotExecuted")
    counters = dict(total=str(len(values)), executed=str(len(values) - skipped), passed=str(passed),
                    failed=str(values.count("Failed")), error="0", timeout="0", aborted="0", inconclusive="0",
                    passedButRunAborted="0", notRunnable="0", notExecuted=str(skipped), disconnected="0",
                    warning="0", completed="0", inProgress="0", pending="0")
    section = ET.SubElement(root, f"{{{NS}}}ResultSummary", outcome="Completed")
    ET.SubElement(section, f"{{{NS}}}Counters", counters)
    return section
