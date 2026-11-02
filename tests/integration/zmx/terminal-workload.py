"""Finite terminal-state fixture, controlled by acknowledged single-byte commands."""
import os
import select
import sys
import time
import tty

tty.setraw(0)
def output(text):
    os.write(1, text.encode())

for index in range(90):
    output(f"HISTORY-{index:03d}\r\n")

def alternate():
    output("\x1b[?1049h\x1b[2J\x1b[H\x1b[38;2;12;34;56mCOLOR-界-😀\x1b[0m"
           "\x1b[?2004h\x1b[?1h\x1b[>1u\x1b[4;7H")

alternate()
while True:
    command = os.read(0, 1)
    if command == b"n":
        output("\x1b[?1049l\r\nNORMAL-RESTORED\r\n")
    elif command == b"a":
        alternate()
    elif command == b"q":
        output("\x1b[6n")
        replies = b""
        until = time.monotonic() + .7
        while time.monotonic() < until:
            if select.select([0], [], [], .05)[0]:
                replies += os.read(0, 4096)
        output("\x1b[8;1HQUERY-REPLIES-" + str(replies.count(b"R")))
