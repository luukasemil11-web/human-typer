"""Human-like typing bot.

Paste text, press Start, click into your Google Doc during the countdown,
and the text gets typed with realistic rhythm, pauses and corrected typos.

Stop at any time with the stop hotkey (Cmd+Shift+9 on Mac, Ctrl+Shift+9 on
Windows), the Esc key, the Stop button, or by slamming the mouse into the
top-left corner of the screen.

Hotkeys are read by polling the physical keyboard state rather than with a
global listener, so no extra permission is needed and nothing runs off the
main thread.

All typing happens on the Tk main thread via root.after(): pynput's macOS
keyboard code is not safe to call from background threads.
"""

import ctypes
import ctypes.util
import random
import sys
import tkinter as tk
from tkinter import ttk

from pynput.keyboard import Controller, Key
from pynput.mouse import Controller as MouseController

# QWERTY neighbours, used to make believable typos.
NEIGHBOURS = {
    "q": "wa", "w": "qeas", "e": "wrds", "r": "etdf", "t": "ryfg", "y": "tugh",
    "u": "yihj", "i": "uojk", "o": "ipkl", "p": "ol", "a": "qwsz", "s": "awedxz",
    "d": "serfcx", "f": "drtgvc", "g": "ftyhbv", "h": "gyujnb", "j": "huikmn",
    "k": "jiolm", "l": "kop", "z": "asx", "x": "zsdc", "c": "xdfv", "v": "cfgb",
    "b": "vghn", "n": "bhjm", "m": "njk",
}
SENTENCE_END = ".!?"
CLAUSE_END = ",;:"


class KeyState:
    """Reads which keys are physically held down right now (Mac and Windows)."""

    def __init__(self):
        self.ok = False
        if sys.platform == "darwin":
            q = ctypes.cdll.LoadLibrary(ctypes.util.find_library("ApplicationServices"))
            q.CGEventSourceKeyState.argtypes = [ctypes.c_int32, ctypes.c_uint16]
            q.CGEventSourceKeyState.restype = ctypes.c_bool
            q.CGEventSourceFlagsState.argtypes = [ctypes.c_int32]
            q.CGEventSourceFlagsState.restype = ctypes.c_uint64
            self.q = q
            self.ok = True
            self.hotkey_name = "Cmd+Shift+9"
        elif sys.platform == "win32":
            self.u = ctypes.windll.user32
            self.ok = True
            self.hotkey_name = "Ctrl+Shift+9"
        else:
            self.hotkey_name = "(not available on this system)"

    # HID system state = real hardware only, so the bot's own keystrokes never count.
    def _mac_key(self, code):
        return self.q.CGEventSourceKeyState(1, code)

    def _mac_flags(self):
        return self.q.CGEventSourceFlagsState(1)

    def _win_key(self, vk):
        return bool(self.u.GetAsyncKeyState(vk) & 0x8000)

    def stop_pressed(self):
        """Stop hotkey or Esc is down."""
        if not self.ok:
            return False
        if sys.platform == "darwin":
            f = self._mac_flags()
            combo = (f & 0x100000) and (f & 0x20000) and self._mac_key(25)  # Cmd, Shift, 9
            return bool(combo or self._mac_key(53))                          # Esc
        combo = self._win_key(0x11) and self._win_key(0x10) and self._win_key(0x39)  # Ctrl, Shift, 9
        return combo or self._win_key(0x1B)                                           # Esc

    def shortcut_modifier_held(self):
        """Cmd/Ctrl/Option/Alt held: typing now would trigger shortcuts like Cmd+A."""
        if not self.ok:
            return False
        if sys.platform == "darwin":
            return bool(self._mac_flags() & (0x100000 | 0x40000 | 0x80000))
        return any(self._win_key(vk) for vk in (0x11, 0x12, 0x5B, 0x5C))


class TypingBot:
    def __init__(self, root):
        self.root = root
        self.kb = Controller()
        self.mouse = MouseController()
        self.keys = KeyState()
        self.queue = []          # list of actions still to perform
        self.running = False
        self.paused = False
        self.typed = 0
        self.total = 0
        self.job = None

        root.title("Human Typer")
        root.geometry("560x520")
        root.minsize(440, 420)

        frm = ttk.Frame(root, padding=12)
        frm.pack(fill="both", expand=True)

        ttk.Label(frm, text="Paste your text:").pack(anchor="w")
        self.text = tk.Text(frm, wrap="word", height=12, undo=True)
        self.text.pack(fill="both", expand=True, pady=(4, 10))

        opts = ttk.Frame(frm)
        opts.pack(fill="x")
        opts.columnconfigure(1, weight=1)

        self.wpm = tk.IntVar(value=55)
        self.typo_rate = tk.DoubleVar(value=3.0)
        self.pause_rate = tk.DoubleVar(value=8.0)
        self.countdown = tk.IntVar(value=5)
        self._slider(opts, 0, "Speed (WPM)", self.wpm, 15, 150, "{:.0f}")
        self._slider(opts, 1, "Typos (% of words)", self.typo_rate, 0, 15, "{:.1f}")
        self._slider(opts, 2, "Thinking pauses (% of sentences)", self.pause_rate, 0, 40, "{:.0f}")
        self._slider(opts, 3, "Countdown (s)", self.countdown, 2, 15, "{:.0f}")

        btns = ttk.Frame(frm)
        btns.pack(fill="x", pady=(10, 4))
        self.start_btn = ttk.Button(btns, text="Start", command=self.start)
        self.start_btn.pack(side="left")
        self.pause_btn = ttk.Button(btns, text="Pause", command=self.toggle_pause, state="disabled")
        self.pause_btn.pack(side="left", padx=6)
        self.stop_btn = ttk.Button(btns, text="Stop", command=self.stop, state="disabled")
        self.stop_btn.pack(side="left")

        self.progress = ttk.Progressbar(frm, mode="determinate")
        self.progress.pack(fill="x", pady=(6, 2))
        self.status = tk.StringVar(
            value=f"Ready. To stop: {self.keys.hotkey_name}, Esc, or mouse to the top-left corner.")
        ttk.Label(frm, textvariable=self.status, foreground="#555").pack(anchor="w")

    def _slider(self, parent, row, label, var, lo, hi, fmt):
        ttk.Label(parent, text=label).grid(row=row, column=0, sticky="w", padx=(0, 8))
        ttk.Scale(parent, from_=lo, to=hi, variable=var, orient="horizontal").grid(
            row=row, column=1, sticky="ew")
        val = ttk.Label(parent, width=5)
        val.grid(row=row, column=2, sticky="e")

        def update(*_):
            v = var.get()
            if isinstance(var, tk.IntVar):
                var.set(int(round(v)))
            val.config(text=fmt.format(var.get()))
        var.trace_add("write", update)
        update()

    # ---------- planning ----------

    def build_actions(self, text):
        """Turn text into a list of ('type', ch) / ('back',) / ('wait', s) actions."""
        actions = []
        typo_p = self.typo_rate.get() / 100
        pause_p = self.pause_rate.get() / 100
        i = 0
        while i < len(text):
            ch = text[i]
            # Possibly fumble a letter at the start of / inside a word.
            if ch.lower() in NEIGHBOURS and random.random() < typo_p / 4:
                wrong = random.choice(NEIGHBOURS[ch.lower()])
                if ch.isupper():
                    wrong = wrong.upper()
                actions.append(("type", wrong))
                # Sometimes the typist keeps going a letter or two before noticing.
                extra = text[i + 1:i + 1 + random.choice([0, 0, 1, 1, 2])]
                extra = extra.split(" ")[0].split("\n")[0]
                for e in extra:
                    actions.append(("type", e))
                actions.append(("wait", random.uniform(0.25, 0.7)))
                for _ in range(1 + len(extra)):
                    actions.append(("back",))
                actions.append(("wait", random.uniform(0.08, 0.25)))
            actions.append(("type", ch))
            if ch in SENTENCE_END and random.random() < pause_p:
                actions.append(("wait", random.uniform(1.5, 4.5)))
            i += 1
        return actions

    def char_delay(self, ch, prev):
        base = 60 / (max(self.wpm.get(), 1) * 5)   # seconds per character
        d = random.gauss(base, base * 0.35)
        if ch == " ":
            d *= random.uniform(1.0, 1.6)
        elif ch == "\n":
            d *= random.uniform(3, 6)
        elif prev in SENTENCE_END:
            d *= random.uniform(2.5, 4.5)
        elif prev in CLAUSE_END:
            d *= random.uniform(1.5, 2.5)
        elif ch.isupper() or not ch.isalnum():
            d *= random.uniform(1.2, 1.8)       # shift / reaching for symbols
        if random.random() < 0.02:
            d += random.uniform(0.3, 1.0)       # random micro-hesitation
        return max(d, 0.015)

    # ---------- control ----------

    def start(self):
        text = self.text.get("1.0", "end-1c").replace("\r\n", "\n")
        if not text.strip():
            self.status.set("Paste some text first.")
            return
        self.queue = self.build_actions(text)
        self.total = len(self.queue)
        self.typed = 0
        self.progress.config(maximum=self.total, value=0)
        self.running = True
        self.paused = False
        self.start_btn.config(state="disabled")
        self.stop_btn.config(state="normal")
        self.pause_btn.config(state="disabled", text="Pause")
        self.text.config(state="disabled")
        self._watch()
        self._countdown(self.countdown.get())

    def _watch(self):
        """Poll for the stop hotkey, including during countdowns and long pauses."""
        if not self.running:
            return
        if self.keys.stop_pressed():
            self.stop("Stopped by hotkey.")
            return
        self.root.after(30, self._watch)

    def _countdown(self, n):
        if not self.running:
            return
        if n <= 0:
            self.status.set(f"Typing… ({self.keys.hotkey_name} or Esc to stop)")
            self.pause_btn.config(state="normal")
            self.prev = ""
            self._step()
            return
        self.status.set(f"Click into your Google Doc now! Starting in {n}…")
        self.job = self.root.after(1000, self._countdown, n - 1)

    def _failsafe(self):
        x, y = self.mouse.position
        return x <= 3 and y <= 3

    def _step(self):
        self.job = None
        if not self.running or self.paused:
            return
        if self._failsafe():
            self.stop("Stopped by failsafe (mouse in corner).")
            return
        if self.keys.stop_pressed():
            self.stop("Stopped by hotkey.")
            return
        if self.keys.shortcut_modifier_held():
            # Never type while Cmd/Ctrl is down, or letters become shortcuts.
            self.job = self.root.after(30, self._step)
            return
        if not self.queue:
            self.stop("Done!")
            return

        action = self.queue.pop(0)
        self.typed += 1
        self.progress.config(value=self.typed)
        delay = 0.0
        if action[0] == "type":
            ch = action[1]
            if ch == "\n":
                self.kb.press(Key.enter)
                self.kb.release(Key.enter)
            elif ch == "\t":
                self.kb.press(Key.tab)
                self.kb.release(Key.tab)
            else:
                self.kb.type(ch)
            delay = self.char_delay(self.queue[0][1] if self.queue and self.queue[0][0] == "type" else " ", ch)
            self.prev = ch
        elif action[0] == "back":
            self.kb.press(Key.backspace)
            self.kb.release(Key.backspace)
            delay = random.uniform(0.06, 0.14)
        elif action[0] == "wait":
            delay = action[1]
        self.job = self.root.after(int(delay * 1000), self._step)

    def toggle_pause(self):
        if not self.running:
            return
        self.paused = not self.paused
        if self.paused:
            if self.job:
                self.root.after_cancel(self.job)
                self.job = None
            self.pause_btn.config(text="Resume")
            self.status.set("Paused. Press Resume, then click back into the doc.")
        else:
            self.pause_btn.config(text="Pause", state="disabled")
            self._resume_countdown(3)

    def _resume_countdown(self, n):
        if not self.running or self.paused:
            return
        if n <= 0:
            self.pause_btn.config(state="normal")
            self.status.set(f"Typing… ({self.keys.hotkey_name} or Esc to stop)")
            self._step()
            return
        self.status.set(f"Click back into the doc! Resuming in {n}…")
        self.job = self.root.after(1000, self._resume_countdown, n - 1)

    def stop(self, msg="Stopped."):
        self.running = False
        self.paused = False
        if self.job:
            self.root.after_cancel(self.job)
            self.job = None
        self.queue = []
        self.start_btn.config(state="normal")
        self.stop_btn.config(state="disabled")
        self.pause_btn.config(state="disabled", text="Pause")
        self.text.config(state="normal")
        self.status.set(msg)


if __name__ == "__main__":
    root = tk.Tk()
    TypingBot(root)
    root.attributes("-topmost", True)
    root.mainloop()
