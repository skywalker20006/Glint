"""
Builds Assets/emoji.txt, the list Glint's emoji search uses:  emoji <TAB> name <TAB> extra keywords

    python tools/make_emoji_list.py

Names come from Python's Unicode database. A character is kept only if Windows' Segoe UI Emoji font draws it
in colour, which is what separates real emoji from the plain symbols that share their Unicode blocks.
Extra keywords (below) add the words people actually type, like "lol" or "love". The order of POPULAR is the
order emoji are listed in when nothing more specific was typed.
"""
import os
import unicodedata

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FONT = ImageFont.truetype(os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", "seguiemj.ttf"), 32)
RANGES = [(0x2000, 0x2BFF), (0x1F000, 0x1FAFF)]

KEYWORDS = {
    "😂": "lol haha laugh funny crying laughing",
    "🤣": "lol rofl laugh funny",
    "😭": "cry crying sad sob",
    "🥺": "please puppy eyes cute",
    "❤": "love heart red",
    "😍": "love heart eyes crush",
    "🥰": "love hearts adore",
    "😘": "kiss love",
    "😊": "happy smile blush",
    "🙂": "smile ok fine",
    "😀": "happy smile grin",
    "😁": "happy grin smile",
    "😅": "sweat nervous laugh phew",
    "😎": "cool sunglasses",
    "🤔": "think thinking hmm",
    "🙄": "eye roll whatever annoyed",
    "😴": "sleep tired zzz",
    "😡": "angry mad rage",
    "😢": "sad cry tear",
    "😱": "scream shocked omg scared",
    "🤯": "mind blown shocked",
    "😬": "grimace awkward yikes",
    "🤗": "hug hugging",
    "🤐": "zip quiet secret",
    "🤫": "shh quiet secret",
    "🙃": "upside down silly sarcasm",
    "😉": "wink",
    "😏": "smirk",
    "🤩": "star struck excited wow",
    "🥳": "party celebrate birthday",
    "🎉": "party celebrate tada congrats",
    "🎂": "birthday cake",
    "🎁": "gift present birthday",
    "👍": "thumbs up yes ok like good",
    "👎": "thumbs down no dislike bad",
    "👏": "clap applause bravo well done",
    "🙏": "pray please thanks thank you hope",
    "🙌": "hooray celebrate raise hands yay",
    "👋": "wave hi hello bye",
    "👌": "ok perfect",
    "✌": "peace victory",
    "🤞": "fingers crossed luck hope",
    "💪": "strong muscle flex gym",
    "👀": "eyes look watching",
    "🤷": "shrug idk dunno whatever",
    "🤦": "facepalm ugh",
    "💀": "skull dead dying lmao",
    "🔥": "fire lit hot flame",
    "✨": "sparkles magic shiny new",
    "⭐": "star favourite favorite",
    "🌟": "star glowing shine",
    "💯": "hundred 100 perfect score",
    "✅": "check done tick yes",
    "❌": "cross no wrong x",
    "⚠": "warning caution",
    "❗": "exclamation important",
    "❓": "question",
    "💡": "idea bulb light",
    "🚀": "rocket launch ship fast",
    "💻": "laptop computer code",
    "📱": "phone mobile",
    "📌": "pin pinned",
    "📎": "paperclip attachment",
    "📅": "calendar date",
    "⏰": "alarm clock time",
    "💰": "money bag cash",
    "💸": "money flying spend",
    "🎵": "music note song",
    "🎶": "music notes song",
    "🎮": "game gaming controller",
    "☕": "coffee tea hot drink",
    "🍕": "pizza food",
    "🍔": "burger food",
    "🍺": "beer drink cheers",
    "🍻": "cheers beers drinks",
    "🌈": "rainbow pride",
    "☀": "sun sunny weather",
    "🌧": "rain weather",
    "❄": "snow cold winter",
    "🌙": "moon night",
    "🐶": "dog puppy pet",
    "🐱": "cat kitty pet",
    "💔": "broken heart sad",
    "💕": "hearts love",
    "💖": "sparkling heart love",
    "💙": "blue heart",
    "💚": "green heart",
    "💜": "purple heart",
    "🖤": "black heart",
    "🤍": "white heart",
    "🧡": "orange heart",
    "💛": "yellow heart",
    "😤": "huff frustrated triumph",
    "🥲": "smile tear happy sad",
    "😇": "angel innocent halo",
    "😈": "devil evil mischief",
    "🤡": "clown",
    "👻": "ghost boo halloween",
    "🎃": "pumpkin halloween",
    "🎄": "christmas tree xmas",
    "🏆": "trophy win winner champion",
    "🥇": "gold medal first winner",
    "⚡": "lightning zap fast electric",
    "🎯": "target goal bullseye",
    "📈": "chart up growth increase stonks",
    "📉": "chart down decrease",
    "🤝": "handshake deal agree",
    "🫡": "salute respect",
    "🫠": "melting embarrassed",
    "😮": "wow surprised oh",
    "😳": "flushed embarrassed shocked",
    "🤓": "nerd geek",
    "🥶": "cold freezing",
    "🥵": "hot sweating",
    "🤢": "sick nauseous gross",
    "🤮": "vomit sick gross",
    "😷": "mask sick",
    "🤒": "sick ill fever",
}
POPULAR = list(KEYWORDS)

# Unicode's character names for the older symbols describe how they were once printed ("heavy black heart").
# These are the names people know them by.
NAMES = {
    "❤": "red heart", "✅": "check mark button", "✔": "check mark", "✖": "multiply", "❎": "cross mark button",
    "☝": "index pointing up", "☹": "frowning face", "☺": "smiling face", "☀": "sun", "⭐": "star",
    "❓": "question mark", "❗": "exclamation mark", "❣": "heart exclamation", "⚧": "transgender symbol",
    "Ⓜ": "circled m", "♻": "recycling symbol", "⛑": "rescue worker helmet", "✴": "eight pointed star",
    "☎": "telephone", "✂": "scissors", "✒": "pen nib", "⏹": "stop button", "⏺": "record button",
    "▶": "play button", "◀": "reverse button", "⏯": "play or pause button", "⏭": "next track button",
    "⏮": "last track button", "⏩": "fast forward button", "⏪": "fast reverse button",
    "⏫": "fast up button", "⏬": "fast down button",
}


def tidy(ch, name):
    """Drops the printing words from the older symbols' names: "black spade suit" -> "spade suit"."""
    if ch in NAMES:
        return NAMES[ch]
    if ord(ch) < 0x1F000:
        for word in ("black ", "white ", "heavy "):
            if name.startswith(word):
                name = name[len(word):]
        for tail in (" ornament", " symbol"):
            if name.endswith(tail) and len(name) > len(tail) + 3:
                name = name[: -len(tail)]
    return name


def is_colour_emoji(ch):
    im = Image.new("RGBA", (48, 48), (0, 0, 0, 0))
    ImageDraw.Draw(im).text((4, 4), ch, font=FONT, embedded_color=True)
    return sum(1 for r, g, b, a in im.get_flattened_data() if a > 100 and max(r, g, b) - min(r, g, b) > 40) > 20


def main():
    rows = {}
    for lo, hi in RANGES:
        for cp in range(lo, hi + 1):
            ch = chr(cp)
            name = unicodedata.name(ch, "")
            if not name or not is_colour_emoji(ch):
                continue
            if name.startswith(("REGIONAL INDICATOR", "EMOJI MODIFIER", "VARIATION SELECTOR")):
                continue
            rows[ch] = tidy(ch, name.lower().replace(" sign", "").replace("-", " "))
    order = [c for c in POPULAR if c in rows] + [c for c in rows if c not in KEYWORDS]
    with open(os.path.join(ROOT, "Assets", "emoji.txt"), "w", encoding="utf-8", newline="\n") as out:
        for ch in order:
            # Symbols from the older Unicode blocks need U+FE0F to be shown as a colour emoji.
            text = ch + "\ufe0f" if ord(ch) < 0x1F000 else ch
            out.write(f"{text}\t{rows[ch]}\t{KEYWORDS.get(ch, '')}\n")
    print(len(order), "emoji")


if __name__ == "__main__":
    main()
