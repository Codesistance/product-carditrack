import re
import pathlib
from fpdf import FPDF


class MarkdownPDF(FPDF):
    def __init__(self):
        super().__init__(orientation="P", unit="mm", format="A4")
        self.set_auto_page_break(auto=True, margin=18)
        self.set_margins(18, 18, 18)
        self.add_page()

    @staticmethod
    def clean_unicode(text):
        replacements = {
            "\u2014": "--", "\u2013": "-", "\u2018": "'", "\u2019": "'",
            "\u201c": '"', "\u201d": '"', "\u2022": "*", "\u00b7": ".",
            "\u2026": "...", "\u00e9": "e", "\u00e0": "a", "\u00e8": "e",
        }
        for char, replacement in replacements.items():
            text = text.replace(char, replacement)
        return text.encode("latin-1", errors="replace").decode("latin-1")

    @classmethod
    def strip_inline(cls, text):
        text = re.sub(r"\*\*(.+?)\*\*", r"\1", text)
        text = re.sub(r"\*(.+?)\*", r"\1", text)
        text = re.sub(r"`(.+?)`", r"\1", text)
        text = re.sub(r"_(.+?)_", r"\1", text)
        text = re.sub(r"\[(.+?)\]\(.+?\)", r"\1", text)
        return cls.clean_unicode(text)

    def render(self, md_text):
        lines = md_text.split("\n")
        i = 0
        while i < len(lines):
            line = lines[i]

            if not line.strip():
                self.ln(2)
                i += 1
                continue

            # Headings
            for level, prefix, size, spacing in [
                (1, "# ", 18, 9),
                (2, "## ", 14, 7),
                (3, "### ", 12, 6),
                (4, "#### ", 10, 5),
            ]:
                if line.startswith(prefix) and not line.startswith(prefix + "#"):
                    if level <= 2:
                        self.ln(3)
                    self.set_font("Helvetica", "B", size)
                    self.set_text_color(20, 20, 20)
                    self.multi_cell(0, spacing, self.clean_unicode(line[len(prefix):].strip()), new_x="LMARGIN", new_y="NEXT")
                    if level <= 2:
                        self.set_draw_color(80 if level == 1 else 150, 80 if level == 1 else 150, 80 if level == 1 else 150)
                        self.line(self.get_x(), self.get_y(), self.w - 18, self.get_y())
                        self.ln(2)
                    self.set_text_color(0, 0, 0)
                    i += 1
                    break
            else:
                # Horizontal rule
                if line.strip() == "---":
                    self.ln(2)
                    self.set_draw_color(180, 180, 180)
                    self.line(18, self.get_y(), self.w - 18, self.get_y())
                    self.ln(3)
                    i += 1
                    continue

                # Code block
                if line.startswith("```"):
                    i += 1
                    code_lines = []
                    while i < len(lines) and not lines[i].startswith("```"):
                        code_lines.append(lines[i])
                        i += 1
                    i += 1
                    self.set_fill_color(245, 245, 245)
                    self.set_font("Courier", "", 7.5)
                    self.set_text_color(50, 50, 50)
                    self.multi_cell(0, 4, self.clean_unicode("\n".join(code_lines)), fill=True, new_x="LMARGIN", new_y="NEXT")
                    self.set_text_color(0, 0, 0)
                    self.ln(1)
                    continue

                # Table
                if line.startswith("|"):
                    table_lines = []
                    while i < len(lines) and lines[i].startswith("|"):
                        table_lines.append(lines[i])
                        i += 1
                    rows = []
                    is_header = True
                    for tl in table_lines:
                        if re.match(r"^\|[\s\-:|]+\|", tl):
                            continue
                        cells = [c.strip() for c in tl.strip("|").split("|")]
                        rows.append((cells, is_header))
                        is_header = False
                    if rows:
                        col_count = max(len(r) for r, _ in rows)
                        col_w = (self.w - 36) / col_count
                        for cells, header in rows:
                            if header:
                                self.set_font("Helvetica", "B", 8)
                                self.set_fill_color(60, 60, 60)
                                self.set_text_color(255, 255, 255)
                            else:
                                self.set_font("Helvetica", "", 8)
                                self.set_fill_color(250, 250, 250)
                                self.set_text_color(0, 0, 0)
                            for cell in cells[:col_count]:
                                plain = self.strip_inline(cell)[:60]
                                self.cell(col_w, 5, plain, border=1, fill=True)
                            self.ln()
                        self.set_text_color(0, 0, 0)
                    self.ln(2)
                    continue

                # List item
                m = re.match(r"^(\s*)([-*+]|\d+\.)\s+(.*)", line)
                if m:
                    indent = len(m.group(1))
                    level = indent // 2
                    content = self.strip_inline(m.group(3))
                    self.set_font("Helvetica", "", 9)
                    self.set_x(18 + level * 4)
                    self.cell(4, 5, "\xb7")
                    self.multi_cell(self.w - 36 - level * 4, 5, content, new_x="LMARGIN", new_y="NEXT")
                    i += 1
                    continue

                # Blockquote
                if line.startswith(">"):
                    content = self.strip_inline(line[1:].strip())
                    self.set_font("Helvetica", "I", 9)
                    self.set_text_color(90, 90, 90)
                    self.set_fill_color(245, 245, 245)
                    self.multi_cell(0, 5, content, fill=True, new_x="LMARGIN", new_y="NEXT")
                    self.set_text_color(0, 0, 0)
                    i += 1
                    continue

                # Normal paragraph
                text = self.strip_inline(line)
                self.set_font("Helvetica", "", 9)
                self.multi_cell(0, 5, text, new_x="LMARGIN", new_y="NEXT")
                i += 1
            continue


BASE = pathlib.Path(__file__).resolve().parent

files = [
    (BASE / "screens.md", BASE / "screens.pdf"),
    (BASE / "user_stories.md", BASE / "user_stories.pdf"),
]

for md_path, pdf_path in files:
    text = md_path.read_text(encoding="utf-8")
    pdf = MarkdownPDF()
    pdf.render(text)
    pdf.output(str(pdf_path))
    size = pdf_path.stat().st_size // 1024
    print(f"Created: {pdf_path} ({size} KB)")
