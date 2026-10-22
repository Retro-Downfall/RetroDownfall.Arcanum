#!/usr/bin/env python3
"""Builds the hand-made OpenDocument fixtures in this folder.

No local tool produces .ods or .odp (LibreOffice is not installed), so these packages are written to the layout
LibreOffice 24.x writes for ODF 1.3: the mimetype entry first and stored, a manifest that lists every part including
the thumbnail, content.xml with the usual automatic styles, and styles.xml with master pages and common styles.

Run from this folder:   python3 -I build-handmade.py
Needs only the Python standard library. Output is deterministic (fixed timestamps).
"""

import zipfile

NS = (
    'xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" '
    'xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" '
    'xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" '
    'xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" '
    'xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" '
    'xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0" '
    'xmlns:xlink="http://www.w3.org/1999/xlink" '
    'xmlns:dc="http://purl.org/dc/elements/1.1/" '
    'xmlns:meta="urn:oasis:names:tc:opendocument:xmlns:meta:1.0" '
    'xmlns:number="urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0" '
    'xmlns:presentation="urn:oasis:names:tc:opendocument:xmlns:presentation:1.0" '
    'xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0" '
    'xmlns:chart="urn:oasis:names:tc:opendocument:xmlns:chart:1.0" '
    'xmlns:dr3d="urn:oasis:names:tc:opendocument:xmlns:dr3d:1.0" '
    'xmlns:math="http://www.w3.org/1998/Math/MathML" '
    'xmlns:form="urn:oasis:names:tc:opendocument:xmlns:form:1.0" '
    'xmlns:script="urn:oasis:names:tc:opendocument:xmlns:script:1.0" '
    'xmlns:ooo="http://openoffice.org/2004/office" '
    'xmlns:ooow="http://openoffice.org/2004/writer" '
    'xmlns:oooc="http://openoffice.org/2004/calc" '
    'xmlns:dom="http://www.w3.org/2001/xml-events" '
    'xmlns:xforms="http://www.w3.org/2002/xforms" '
    'xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
    'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
    'xmlns:rpt="http://openoffice.org/2005/report" '
    'xmlns:of="urn:oasis:names:tc:opendocument:xmlns:of:1.2" '
    'xmlns:xhtml="http://www.w3.org/1999/xhtml" '
    'xmlns:grddl="http://www.w3.org/2003/g/data-view#" '
    'xmlns:tableooo="http://openoffice.org/2009/table" '
    'xmlns:calcext="urn:org:documentfoundation:names:experimental:calc:xmlns:calcext:1.0" '
    'xmlns:drawooo="http://openoffice.org/2010/draw" '
    'xmlns:loext="urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0" '
    'xmlns:field="urn:openoffice:names:experimental:ooo-ms-interop:xmlns:field:1.0" '
    'xmlns:formx="urn:openoffice:names:experimental:ooxml-odf-interop:xmlns:form:1.0" '
    'xmlns:css3t="http://www.w3.org/TR/css3-text/"'
)

XML = '<?xml version="1.0" encoding="UTF-8"?>\n'

OPENOFFICE_DOCTYPE = '<!DOCTYPE manifest:manifest PUBLIC "-//OpenOffice.org//DTD Manifest 1.0//EN" "Manifest.dtd">\n'

# A 1x1 transparent PNG, standing in for the thumbnail LibreOffice embeds.
PNG = bytes.fromhex(
    '89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c489'
    '0000000d49444154789c6360000002000001e221bc330000000049454e44ae426082'
)

FONT_DECLS = (
    '<office:font-face-decls>'
    '<style:font-face style:name="Liberation Sans" svg:font-family="\'Liberation Sans\'" '
    'style:font-family-generic="swiss" style:font-pitch="variable"/>'
    '</office:font-face-decls>'
)


def manifest(mime, parts, doctype=False):
    entries = [f' <manifest:file-entry manifest:full-path="/" manifest:version="1.3" manifest:media-type="{mime}"/>']
    for name, media in parts:
        entries.append(f' <manifest:file-entry manifest:full-path="{name}" manifest:media-type="{media}"/>')
    return (
        XML
        + (OPENOFFICE_DOCTYPE if doctype else '')
        + '<manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.3">\n'
        + '\n'.join(entries)
        + '\n</manifest:manifest>'
    )


def meta(generator):
    return (
        XML
        + f'<office:document-meta {NS} office:version="1.3"><office:meta>'
        + f'<meta:generator>{generator}</meta:generator>'
        + '<dc:title>METADATATITLEMARKER</dc:title>'
        + '<dc:creator>METADATACREATORMARKER</dc:creator>'
        + '<meta:creation-date>2026-10-22T09:00:00</meta:creation-date>'
        + '</office:meta></office:document-meta>'
    )


SETTINGS = (
    XML
    + f'<office:document-settings {NS} office:version="1.3"><office:settings>'
    + '<config:config-item-set xmlns:config="urn:oasis:names:tc:opendocument:xmlns:config:1.0" config:name="ooo:view-settings">'
    + '<config:config-item config:name="SETTINGSMARKER" config:type="string">SETTINGSMARKER</config:config-item>'
    + '</config:config-item-set></office:settings></office:document-settings>'
)

# ------------------------------------------------------------------------------------------------ spreadsheet

ODS_STYLES = (
    XML
    + f'<office:document-styles {NS} office:version="1.3">'
    + FONT_DECLS
    + '<office:styles>'
    + '<style:default-style style:family="table-cell"><style:table-cell-properties style:decimal-places="2"/>'
    + '<style:paragraph-properties style:tab-stop-distance="1.25cm"/><style:text-properties style:font-name="Liberation Sans"/></style:default-style>'
    + '<style:style style:name="Default" style:family="table-cell"/>'
    + '<style:style style:name="Heading" style:family="table-cell" style:parent-style-name="Default"><style:text-properties fo:font-weight="bold"/></style:style>'
    + '</office:styles>'
    + '<office:automatic-styles>'
    + '<style:page-layout style:name="Mpm1"><style:page-layout-properties style:writing-mode="lr-tb"/>'
    + '<style:header-style/><style:footer-style/></style:page-layout>'
    + '</office:automatic-styles>'
    + '<office:master-styles><style:master-page style:name="Default" style:page-layout-name="Mpm1">'
    + '<style:header><text:p><text:sheet-name>???</text:sheet-name>HEADERMARKER</text:p></style:header><style:header-left style:display="false"/>'
    + '<style:footer><text:p>Page <text:page-number>1</text:page-number>FOOTERMARKER</text:p></style:footer><style:footer-left style:display="false"/>'
    + '</style:master-page></office:master-styles>'
    + '</office:document-styles>'
)

ODS_AUTOMATIC_STYLES = (
    '<office:automatic-styles>'
    '<style:style style:name="co1" style:family="table-column"><style:table-column-properties fo:break-before="auto" style:column-width="2.258cm"/></style:style>'
    '<style:style style:name="ro1" style:family="table-row"><style:table-row-properties style:row-height="0.452cm" fo:break-before="auto" style:use-optimal-row-height="true"/></style:style>'
    '<style:style style:name="ta1" style:family="table" style:master-page-name="Default"><style:table-properties table:display="true" style:writing-mode="lr-tb"/></style:style>'
    '<style:style style:name="ta2" style:family="table" style:master-page-name="Default"><style:table-properties table:display="false" style:writing-mode="lr-tb"/></style:style>'
    '<style:style style:name="ce1" style:family="table-cell" style:parent-style-name="Heading"/>'
    '<style:style style:name="ce2" style:family="table-cell" style:parent-style-name="Default"><style:text-properties fo:color="#ff0000"/></style:style>'
    '</office:automatic-styles>'
)


def string_cell(text, style='', extra=''):
    style_attr = f' table:style-name="{style}"' if style else ''
    return (
        f'<table:table-cell{style_attr}{extra} office:value-type="string" calcext:value-type="string">'
        f'<text:p>{text}</text:p></table:table-cell>'
    )


def float_cell(value, display, formula=''):
    formula_attr = f' table:formula="{formula}"' if formula else ''
    return (
        f'<table:table-cell{formula_attr} office:value-type="float" office:value="{value}" calcext:value-type="float">'
        f'<text:p>{display}</text:p></table:table-cell>'
    )


def row(cells, extra=''):
    return f'<table:table-row table:style-name="ro1"{extra}>{cells}</table:table-row>'


def ods_content():
    columns = (
        '<table:table-column table:style-name="co1" table:number-columns-repeated="2" table:default-cell-style-name="Default"/>'
        '<table:table-column table:style-name="co1" table:visibility="collapse" table:default-cell-style-name="Default"/>'
        '<table:table-column table:style-name="co1" table:number-columns-repeated="1021" table:default-cell-style-name="Default"/>'
    )
    inventory_rows = (
        row(string_cell('Item', 'ce1') + string_cell('Quantity', 'ce1') + string_cell('SECRETCOLUMNHEADER', 'ce1'))
        + row(string_cell('Widget') + float_cell('12', '12') + string_cell('SECRETCOLUMNVALUE'))
        + row(string_cell('Gadget') + float_cell('7', '7') + string_cell('SECRETCOLUMNVALUE'))
        + row(string_cell('HIDDENROWTEXT') + float_cell('99', '99'), ' table:visibility="collapse"')
        + row(string_cell('FILTEREDROWTEXT') + float_cell('98', '98'), ' table:visibility="filter"')
        + row(
            string_cell('Total')
            + float_cell('19', '19', 'of:=SUM([.B2:.B3])+0*FORMULAMARKER')
        )
        + row(
            '<table:table-cell table:number-columns-spanned="2" table:number-rows-spanned="1" office:value-type="string" calcext:value-type="string">'
            '<office:annotation office:display="false" draw:style-name="gr1"><dc:creator>Reviewer</dc:creator><dc:date>2026-10-22T09:30:00</dc:date>'
            '<text:p>CELLNOTEMARKER</text:p></office:annotation><text:p>Merged heading</text:p></table:table-cell>'
            '<table:covered-table-cell table:number-columns-repeated="1"><text:p>COVEREDCELLMARKER</text:p></table:covered-table-cell>'
            + string_cell('After merge')
            + string_cell('Tail')
        )
        + row(
            '<table:table-cell office:value-type="string" calcext:value-type="string"><text:p>Two</text:p><text:p>paragraphs</text:p></table:table-cell>'
        )
        + '<table:table-row table:style-name="ro1" table:number-rows-repeated="1048567"><table:table-cell table:number-columns-repeated="1024"/></table:table-row>'
    )
    archive_rows = row(string_cell('HIDDENSHEETTEXT'))
    return (
        XML
        + f'<office:document-content {NS} office:version="1.3">'
        + '<office:scripts/>'
        + FONT_DECLS
        + ODS_AUTOMATIC_STYLES
        + '<office:body><office:spreadsheet>'
        + '<table:calculation-settings table:automatic-find-labels="false" table:use-regular-expressions="false" table:use-wildcards="true"/>'
        + f'<table:table table:name="Inventory" table:style-name="ta1">{columns}{inventory_rows}</table:table>'
        + f'<table:table table:name="Archive" table:style-name="ta2"><table:table-column table:style-name="co1" table:number-columns-repeated="1024" table:default-cell-style-name="Default"/>{archive_rows}</table:table>'
        + '<table:named-expressions/>'
        + '</office:spreadsheet></office:body></office:document-content>'
    )


def build_ods(path, doctype):
    mime = 'application/vnd.oasis.opendocument.spreadsheet'
    parts = [
        ('Configurations2/', 'application/vnd.sun.xml.ui.configuration'),
        ('Thumbnails/thumbnail.png', 'image/png'),
        ('settings.xml', 'text/xml'),
        ('content.xml', 'text/xml'),
        ('meta.xml', 'text/xml'),
        ('styles.xml', 'text/xml'),
    ]
    write(
        path,
        mime,
        [
            ('META-INF/manifest.xml', manifest(mime, parts, doctype)),
            ('settings.xml', SETTINGS),
            ('content.xml', ods_content()),
            ('meta.xml', meta('LibreOffice/24.8.0.3$Linux_X86_64 handmade')),
            ('styles.xml', ODS_STYLES),
        ],
    )


# ------------------------------------------------------------------------------------------------ presentation

ODP_STYLES = (
    XML
    + f'<office:document-styles {NS} office:version="1.3">'
    + FONT_DECLS
    + '<office:styles>'
    + '<style:default-style style:family="graphic"><style:graphic-properties draw:shadow-offset-x="0.3cm" draw:shadow-offset-y="0.3cm"/>'
    + '<style:paragraph-properties style:tab-stop-distance="1.25cm"/><style:text-properties style:font-name="Liberation Sans"/></style:default-style>'
    + '<style:style style:name="standard" style:family="graphic"><style:graphic-properties draw:stroke="solid"/></style:style>'
    + '<style:style style:name="Default-title" style:family="presentation"><style:graphic-properties draw:fill="none"/></style:style>'
    + '<style:style style:name="Default-outline1" style:family="presentation"><style:graphic-properties draw:fill="none"/></style:style>'
    + '<style:style style:name="Default-notes" style:family="presentation"><style:graphic-properties draw:fill="none"/></style:style>'
    + '</office:styles>'
    + '<office:automatic-styles>'
    + '<style:page-layout style:name="PM1"><style:page-layout-properties fo:page-width="28cm" fo:page-height="15.75cm"/></style:page-layout>'
    + '</office:automatic-styles>'
    + '<office:master-styles>'
    + '<style:master-page style:name="Default" style:page-layout-name="PM1" draw:style-name="dp1">'
    + '<draw:frame draw:layer="backgroundobjects" svg:width="5cm" svg:height="1cm" svg:x="1cm" svg:y="14cm" presentation:class="footer">'
    + '<draw:text-box><text:p>MASTERFOOTERMARKER</text:p></draw:text-box></draw:frame>'
    + '<presentation:notes style:page-layout-name="PM1"><draw:frame presentation:class="notes"><draw:text-box><text:p>MASTERNOTESMARKER</text:p></draw:text-box></draw:frame></presentation:notes>'
    + '</style:master-page></office:master-styles>'
    + '</office:document-styles>'
)

ODP_AUTOMATIC_STYLES = (
    '<office:automatic-styles>'
    '<style:style style:name="dp1" style:family="drawing-page"><style:drawing-page-properties presentation:background-visible="true" presentation:background-objects-visible="true" presentation:display-footer="true" presentation:display-page-number="false" presentation:display-date-time="true"/></style:style>'
    '<style:style style:name="dp2" style:family="drawing-page"><style:drawing-page-properties presentation:visibility="hidden" presentation:background-visible="true" presentation:background-objects-visible="true"/></style:style>'
    '<style:style style:name="dp3" style:family="drawing-page"><style:drawing-page-properties presentation:background-visible="true" presentation:background-objects-visible="true"/></style:style>'
    '<style:style style:name="gr1" style:family="graphic" style:parent-style-name="standard"><style:graphic-properties draw:display="none"/></style:style>'
    '<style:style style:name="pr1" style:family="presentation" style:parent-style-name="Default-title"><style:graphic-properties draw:fill-color="#ffffff" presentation:user-transformed="true"/></style:style>'
    '<style:style style:name="pr2" style:family="presentation" style:parent-style-name="Default-outline1"><style:graphic-properties draw:fill-color="#ffffff"/></style:style>'
    '<style:style style:name="pr3" style:family="presentation" style:parent-style-name="Default-notes"><style:graphic-properties draw:fill-color="#ffffff"/></style:style>'
    '<style:style style:name="P1" style:family="paragraph"><style:text-properties fo:font-size="44pt"/></style:style>'
    '<style:style style:name="T1" style:family="text"><style:text-properties fo:font-weight="bold"/></style:style>'
    '<text:list-style style:name="L2"><text:list-level-style-bullet text:level="1" text:bullet-char="●"/></text:list-style>'
    '</office:automatic-styles>'
)


def odp_content():
    frame_attrs = 'draw:layer="layout" svg:width="25.2cm" svg:height="3.1cm" svg:x="1.4cm" svg:y="0.8cm"'
    slide_one = (
        '<draw:page draw:name="page1" draw:style-name="dp1" draw:master-page-name="Default" presentation:presentation-page-layout-name="AL1T0">'
        f'<draw:frame presentation:style-name="pr1" draw:text-style-name="P1" {frame_attrs} presentation:class="title" presentation:user-transformed="true">'
        '<draw:text-box><text:p><text:span text:style-name="T1">Quarterly Review</text:span></text:p></draw:text-box></draw:frame>'
        f'<draw:frame presentation:style-name="pr2" {frame_attrs} presentation:class="outline" presentation:user-transformed="true">'
        '<draw:text-box><text:list text:style-name="L2"><text:list-item><text:p>Revenue grew 12%</text:p></text:list-item>'
        '<text:list-item><text:p>Costs held flat<text:s text:c="2"/>year on year</text:p></text:list-item></text:list></draw:text-box></draw:frame>'
        '<draw:custom-shape draw:style-name="gr1" draw:layer="layout" svg:width="5cm" svg:height="2cm" svg:x="20cm" svg:y="10cm">'
        '<text:p>HIDDENSHAPEMARKER</text:p><draw:enhanced-geometry svg:viewBox="0 0 21600 21600" draw:type="rectangle"/></draw:custom-shape>'
        '<draw:custom-shape draw:layer="layout" svg:width="5cm" svg:height="2cm" svg:x="2cm" svg:y="10cm">'
        '<text:p>Callout</text:p><draw:enhanced-geometry svg:viewBox="0 0 21600 21600" draw:type="rectangle"/></draw:custom-shape>'
        '<office:annotation draw:style-name="gr2"><dc:creator>Reviewer</dc:creator><dc:date>2026-10-22T09:30:00</dc:date><text:p>SLIDECOMMENTMARKER</text:p></office:annotation>'
        '<presentation:notes draw:style-name="dp3"><draw:page-thumbnail draw:layer="layout" presentation:class="page" draw:page-number="1"/>'
        f'<draw:frame presentation:style-name="pr3" {frame_attrs} presentation:class="notes"><draw:text-box><text:p>SPEAKERNOTESMARKER</text:p></draw:text-box></draw:frame></presentation:notes>'
        '</draw:page>'
    )
    slide_two = (
        '<draw:page draw:name="page2" draw:style-name="dp2" draw:master-page-name="Default" presentation:presentation-page-layout-name="AL1T0">'
        f'<draw:frame presentation:style-name="pr1" {frame_attrs} presentation:class="title"><draw:text-box><text:p>HIDDENSLIDEMARKER</text:p></draw:text-box></draw:frame>'
        '</draw:page>'
    )
    slide_three = (
        '<draw:page draw:name="page3" draw:style-name="dp3" draw:master-page-name="Default" presentation:presentation-page-layout-name="AL1T0">'
        '<draw:frame draw:layer="layout" svg:width="20cm" svg:height="4cm" svg:x="2cm" svg:y="3cm">'
        '<table:table table:name="Table1"><table:table-column table:number-columns-repeated="2"/>'
        '<table:table-row><table:table-cell office:value-type="string"><text:p>Region</text:p></table:table-cell><table:table-cell office:value-type="string"><text:p>Sales</text:p></table:table-cell></table:table-row>'
        '<table:table-row><table:table-cell office:value-type="string"><text:p>North</text:p></table:table-cell><table:table-cell office:value-type="string"><text:p>41</text:p></table:table-cell></table:table-row>'
        '</table:table></draw:frame>'
        '<draw:frame draw:layer="layout" svg:width="4cm" svg:height="4cm" svg:x="2cm" svg:y="9cm">'
        '<draw:image xlink:href="Pictures/chart.png" xlink:type="simple" xlink:show="embed" xlink:actuate="onLoad"><text:p/></draw:image>'
        '<svg:title>IMAGETITLEMARKER</svg:title><svg:desc>IMAGEDESCRIPTIONMARKER</svg:desc></draw:frame>'
        '</draw:page>'
    )
    return (
        XML
        + f'<office:document-content {NS} office:version="1.3">'
        + '<office:scripts/>'
        + FONT_DECLS
        + ODP_AUTOMATIC_STYLES
        + '<office:body><office:presentation>'
        + '<presentation:footer-decl presentation:name="ftr1">DECLAREDFOOTERMARKER</presentation:footer-decl>'
        + '<presentation:date-time-decl presentation:name="dtd1" presentation:source="fixed" style:data-style-name="D1">DECLAREDDATEMARKER</presentation:date-time-decl>'
        + slide_one + slide_two + slide_three
        + '<presentation:settings presentation:mouse-visible="false"/>'
        + '</office:presentation></office:body></office:document-content>'
    )


def build_odp(path, doctype):
    mime = 'application/vnd.oasis.opendocument.presentation'
    parts = [
        ('Configurations2/', 'application/vnd.sun.xml.ui.configuration'),
        ('Thumbnails/thumbnail.png', 'image/png'),
        ('settings.xml', 'text/xml'),
        ('content.xml', 'text/xml'),
        ('meta.xml', 'text/xml'),
        ('styles.xml', 'text/xml'),
        ('Pictures/chart.png', 'image/png'),
    ]
    write(
        path,
        mime,
        [
            ('META-INF/manifest.xml', manifest(mime, parts, doctype)),
            ('settings.xml', SETTINGS),
            ('content.xml', odp_content()),
            ('meta.xml', meta('LibreOffice/24.8.0.3$Linux_X86_64 handmade')),
            ('styles.xml', ODP_STYLES),
        ],
        extra_binary=[('Pictures/chart.png', PNG)],
    )


# ------------------------------------------------------------------------------------------------ writer

def write(path, mime, text_entries, extra_binary=()):
    stamp = (2026, 10, 22, 9, 0, 0)
    with zipfile.ZipFile(path, 'w') as package:
        first = zipfile.ZipInfo('mimetype', date_time=stamp)
        first.compress_type = zipfile.ZIP_STORED
        package.writestr(first, mime)
        for name in ('Configurations2/',):
            info = zipfile.ZipInfo(name, date_time=stamp)
            info.external_attr = 0o40755 << 16
            package.writestr(info, b'')
        thumbnail = zipfile.ZipInfo('Thumbnails/thumbnail.png', date_time=stamp)
        thumbnail.compress_type = zipfile.ZIP_STORED
        package.writestr(thumbnail, PNG)
        for name, data in extra_binary:
            info = zipfile.ZipInfo(name, date_time=stamp)
            info.compress_type = zipfile.ZIP_STORED
            package.writestr(info, data)
        for name, text in text_entries:
            info = zipfile.ZipInfo(name, date_time=stamp)
            info.compress_type = zipfile.ZIP_DEFLATED
            package.writestr(info, text.encode('utf-8'))


if __name__ == '__main__':
    build_ods('handmade-libreoffice-style.ods', doctype=False)
    build_ods('handmade-doctype-manifest.ods', doctype=True)
    build_odp('handmade-libreoffice-style.odp', doctype=False)
    build_odp('handmade-doctype-manifest.odp', doctype=True)
