import sys

import xlsxwriter

# Usage: python3 -I generate-xlsxwriter-basic.py <output.xlsx>
workbook = xlsxwriter.Workbook(sys.argv[1])

visible = workbook.add_worksheet("Shared")
bold = workbook.add_format({"bold": True})
red = workbook.add_format({"font_color": "red"})
visible.write_string("A1", "Plain")
visible.write_rich_string("B1", "Rich ", bold, "bold", " and ", red, "red")
visible.write_number("A2", 3.5)
visible.write_formula("B2", "=A2*2", None, 7)
visible.write_boolean("A3", True)
visible.write_boolean("B3", False)
visible.write_formula("A4", '=1/0', None, "#DIV/0!")
visible.write_string("A5", "HIDDENROWTEXT")
visible.set_row(4, None, None, {"hidden": True})
visible.write_string("C1", "HIDDENCOLUMNTEXT")
visible.set_column("C:C", None, None, {"hidden": True})
visible.write_string("D1", "after")
visible.write_comment("A1", "COMMENTTEXT")

hidden = workbook.add_worksheet("Hidden")
hidden.write_string("A1", "HIDDENSHEETTEXT")
hidden.hide()

workbook.close()
