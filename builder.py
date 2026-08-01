import argparse
import shutil

from pathlib import Path
from jinja2 import Environment, FileSystemLoader, select_autoescape

USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36"

def add_arguments(parser):
    builder = parser.add_argument_group("Builder options")
    builder.add_argument("--name", default="MessengerClient",
                     help="Name of the output directory.")

    cfg = parser.add_argument_group("Client configuration")
    cfg.add_argument("--server-url", default="localhost:8080",
                     help="Server URL the client should connect to.")
    cfg.add_argument("-e", "--encryption-key", default="",
                     help="AES encryption key to embed (optional).")
    cfg.add_argument("--user-agent", default=USER_AGENT,
                     help="Custom HTTP/WebSocket User-Agent string (optional).")
    cfg.add_argument("--proxy", default="",
                     help="Proxy to use (optional).")
    cfg.add_argument("--remote-port-forwards", nargs="*", default=[],
                     help="Space delimited remote port forwards LISTENING-IP:LISTENING-PORT:REMOTE-IP:REMOTE-PORT (optional).")

    retry = parser.add_argument_group("Retry behavior")
    retry.add_argument("--retry-duration", type=float, default=60.0,
                       help="Total time to retry connecting.")
    retry.add_argument("--retry-attempts", type=int, default=5,
                       help="Number of retry attempts before giving up.")


def build(args):
    template_dir = Path(__file__).resolve().parent / "templates"
    if not template_dir.is_dir():
        raise RuntimeError(f"Template directory not found: {template_dir}")

    env = Environment(
        loader=FileSystemLoader(str(template_dir)),
        autoescape=select_autoescape(enabled_extensions=("j2",)),
        trim_blocks=True,
        lstrip_blocks=True,
    )

    out_dir = Path(args.name)

    for src in sorted(template_dir.rglob("*")):
        if src.is_dir():
            continue
        rel = src.relative_to(template_dir)
        dest = out_dir / rel
        dest.parent.mkdir(parents=True, exist_ok=True)

        if src.suffix == ".cs":
            template = env.get_template(str(rel).replace("\\", "/"))
            rendered = template.render(**vars(args))
            dest.write_text(rendered, encoding="utf-8")
        else:
            shutil.copy2(src, dest)

    print(f"[+] Wrote C# client to '{out_dir}'")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(usage=argparse.SUPPRESS)
    add_arguments(parser)
    parsed_args = parser.parse_args()
    build(parsed_args)
