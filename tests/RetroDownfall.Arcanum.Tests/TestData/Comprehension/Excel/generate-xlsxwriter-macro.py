import os
import sys
import tempfile

import xlsxwriter

# Usage: python3 -I generate-xlsxwriter-macro.py <output.xlsm>
# xlsxwriter embeds whatever bytes it is given as xl/vbaProject.bin without parsing them. The placeholder below is NOT a
# real VBA project: the fixture proves the .xlsm package layout (macro-enabled main part and vbaProject relationship) and
# that the extractor reads the cells without ever opening the macro part.
with tempfile.TemporaryDirectory() as scratch:
    vba_path = os.path.join(scratch, "vbaProject.bin")
    with open(vba_path, "wb") as vba_file:
        vba_file.write(b"PLACEHOLDER-NOT-A-REAL-VBA-PROJECT-MACROTEXT" * 4)

    workbook = xlsxwriter.Workbook(sys.argv[1])
    workbook.add_vba_project(vba_path)
    sheet = workbook.add_worksheet("Macros")
    sheet.write_string("A1", "macro-enabled")
    sheet.write_number("B1", 12)
    workbook.close()
