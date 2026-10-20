"""Writes the GitHub release body for a tag from its docs release notes page.

Usage: release-notes.py <tag> <output>

Reads docs/articles/releases/release-notes-<tag>.md, drops the page title and the "Released" line, and rewrites
relative links to .md pages as absolute links to the documentation site. Fails if the page does not exist, so a
release can't be published without notes.
"""
import posixpath
import re
import sys

SITE = 'https://dtronix.github.io/NexNet/'
PAGE_DIR = 'articles/releases'


def absolute(target):
    if re.match(r'^[a-z][a-z0-9+.-]*:|^#|^/', target, re.IGNORECASE):
        return target  # already absolute, an anchor, or site-rooted
    path, sep, anchor = target.partition('#')
    if not path.endswith('.md'):
        return target
    resolved = posixpath.normpath(posixpath.join(PAGE_DIR, path))
    return SITE + resolved[:-3] + '.html' + sep + anchor


def main():
    tag, output = sys.argv[1], sys.argv[2]
    source = f'docs/articles/releases/release-notes-{tag}.md'
    try:
        with open(source, encoding='utf-8') as f:
            lines = f.read().replace('\r\n', '\n').split('\n')
    except FileNotFoundError:
        print(f'::error::No release notes for {tag}: {source} does not exist.')
        return 1

    # Drop the "# NexNet vX.Y.Z" title and the "*Released ...*" line; GitHub shows its own title and date.
    if lines and lines[0].startswith('# '):
        lines = lines[1:]
    lines = [line for line in lines if not re.match(r'^\*(Released|Unreleased)\b.*\*$', line.strip())]
    body = '\n'.join(lines).strip() + '\n'
    body = re.sub(r'\]\(([^)\s]+)\)', lambda m: '](' + absolute(m.group(1)) + ')', body)

    with open(output, 'w', encoding='utf-8', newline='\n') as f:
        f.write(body)
    return 0


if __name__ == '__main__':
    sys.exit(main())
