import fcntl, os, pty, re, select, struct, sys, termios, time

# Run the packaged helper in a real terminal: check (no osu! session, so no network), open Add gamemode, type, back out, quit.
helper = sys.argv[1]
pid, master = pty.fork()
if pid == 0:
    os.environ["TERM"] = "xterm-256color"
    os.execv(helper, [helper])
try:
    fcntl.ioctl(master, termios.TIOCSWINSZ, struct.pack("HHHH", 24, 80, 0, 0))
except OSError:
    pass  # the helper falls back to 80x24
output = b""


def pump(seconds):
    global output
    end = time.time() + seconds
    while time.time() < end:
        if select.select([master], [], [], 0.1)[0]:
            try:
                output += os.read(master, 65536)
            except OSError:
                return


for keys, wait in [(b"", 2), (b"\r", 2), (b"\x1b[B", 0.5), (b"\r", 0.5), (b"owner/repo", 0.5), (b"\x1b", 1), (b"q", 3)]:
    os.write(master, keys)
    pump(wait)
_, status = os.waitpid(pid, 0)
text = re.sub(rb"\x1b\[[0-9;?]*[A-Za-z]", b"", output).decode("utf-8", "replace")
checks = {
    "exits cleanly": os.waitstatus_to_exitcode(status) == 0,
    "shows the menu": "Check for updates" in text and "Add gamemode" in text,
    "runs a check": "Launch osu! once" in text,
    "edits a repository": "GitHub repository" in text and "owner/repo" in text,
    "restores the terminal": b"\x1b[?1049l" in output,
}
for name, ok in checks.items():
    print(("ok   " if ok else "FAIL ") + name)
if not all(checks.values()):
    print(text[-2000:])
    sys.exit(1)
print("Terminal menu works in a real terminal")
