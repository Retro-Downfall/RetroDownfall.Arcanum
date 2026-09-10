from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path


SCRIPT_PATH = Path(__file__).resolve().parents[1] / "align_csharp_blanklines.py"
SPEC = importlib.util.spec_from_file_location("align_csharp_blanklines", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
FORMATTER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(FORMATTER)


class AlignCSharpBlankLinesTests(unittest.TestCase):
    def test_removes_blank_lines_just_inside_code_delimiters(self) -> None:
        source = '''using System;

namespace Example;

internal sealed class Sample
{

    public void Run(

        string value)
    {

        string[] values =
        [

            value,

        ];

        Consume(

            values);

    }

}
'''
        expected = '''using System;

namespace Example;

internal sealed class Sample
{
    public void Run(
        string value)
    {
        string[] values =
        [
            value,
        ];

        Consume(
            values);
    }
}
'''

        self.assertEqual(expected, self._format(source))

    def test_preserves_delimiter_shaped_blank_lines_inside_multiline_literals_and_comments(self) -> None:
        source = '''namespace Example;

internal static class Sample
{
    private const string Raw = """
{

}
""";

    /*
    [

    ]
    */
    public static string Value => Raw;
}
'''

        self.assertEqual(source, self._format(source))

    def test_formatting_is_idempotent(self) -> None:
        source = '''namespace Example;

internal sealed class Sample
{

    public void Run()
    {

        Act();

    }

}
'''

        once = self._format(source)
        twice = self._format(once)

        self.assertEqual(once, twice)

    @staticmethod
    def _format(source: str) -> str:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "Sample.cs"
            path.write_text(source, encoding="utf-8")
            changed = FORMATTER.process_file(
                path,
                dry_run=False,
                compact_delimiters=True,
            )
            result = path.read_text(encoding="utf-8")

        if source != result:
            assert changed

        return result


if __name__ == "__main__":
    unittest.main()
