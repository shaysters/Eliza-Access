"""Render README.md to HTML and add it to the release zip.

Usage: readme_html.py <README.md> <zip>

Adds ElizaAccess-README.html beside the ElizaAccess-README.md the build
already put in the zip, so players can read the guide in a browser with
real headings and tables. Run by build_release.sh through
`uv run --with markdown==3.7`.
"""

import html
import sys
import zipfile

import markdown

ENTRY = "ElizaAccess-README.html"

PAGE = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{title}</title>
<style>
body {{ font-family: system-ui, sans-serif; line-height: 1.5; max-width: 50rem; margin: 2rem auto; padding: 0 1rem; }}
table {{ border-collapse: collapse; }}
th, td {{ border: 1px solid #888; padding: 0.3rem 0.6rem; text-align: left; vertical-align: top; }}
code, pre {{ background: #f2f2f2; }}
pre {{ padding: 0.5rem; overflow-x: auto; }}
@media (prefers-color-scheme: dark) {{
  body {{ background: #1e1e1e; color: #e6e6e6; }}
  a {{ color: #8ab4f8; }}
  code, pre {{ background: #2d2d2d; }}
}}
</style>
</head>
<body>
{body}
</body>
</html>
"""


def main() -> None:
    readme_path, zip_path = sys.argv[1], sys.argv[2]
    with open(readme_path, encoding="utf-8") as f:
        text = f.read()

    title = "Eliza Access"
    for line in text.splitlines():
        if line.startswith("# "):
            title = line[2:].strip()
            break

    body = markdown.markdown(text, extensions=["tables", "fenced_code"])
    page = PAGE.format(title=html.escape(title), body=body)

    with zipfile.ZipFile(zip_path, "a", compression=zipfile.ZIP_DEFLATED) as z:
        if ENTRY in z.namelist():
            sys.exit(f"{zip_path} already contains {ENTRY}")
        z.writestr(ENTRY, page)
    print(f"Added {ENTRY} to {zip_path}")


if __name__ == "__main__":
    main()
