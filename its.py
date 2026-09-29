"""Detect the build host's OS so the builder can print instructions the
operator can actually paste. The builder itself doesn't compile anything --
these flags only shape the next-step hints.

Named so the call site reads like a sentence:  if its.windows:
"""
import sys

windows = sys.platform == "win32"
linux = sys.platform.startswith("linux")
macos = sys.platform == "darwin"
