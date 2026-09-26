from __future__ import annotations

import re


_ARABIC_RE = re.compile(r"[\u0600-\u06ff]")
_ARABIC_DIACRITICS_RE = re.compile(r"[\u0610-\u061a\u064b-\u065f\u0670\u06d6-\u06ed]")
_ARABIC_DIGITS = str.maketrans("٠١٢٣٤٥٦٧٨٩۰۱۲۳۴۵۶۷۸۹", "01234567890123456789")


def contains_arabic(text: str | None) -> bool:
    return bool(_ARABIC_RE.search(text or ""))


def normalize_arabic(text: str | None) -> str:
    value = (text or "").translate(_ARABIC_DIGITS)
    value = _ARABIC_DIACRITICS_RE.sub("", value)
    replacements = {
        "أ": "ا",
        "إ": "ا",
        "آ": "ا",
        "ٱ": "ا",
        "ى": "ي",
        "ئ": "ي",
        "ؤ": "و",
        "ة": "ه",
        "ـ": "",
    }
    for src, dst in replacements.items():
        value = value.replace(src, dst)
    return re.sub(r"\s+", " ", value).strip().lower()


_FRANKO_HINTS = (
    r"\b(?:a7san|ahsan|afdal|aktr|aktar|a3la|meen|men|eh|eih|el|elly|3ayz|3awez|عايز)\b",
    r"\b(?:la3eb|la3eba|le3ba|fere2|fare2|match|matsh|gamed|kora|koora)\b",
    r"\b(?:no2at|nokat|nuqat|asist|assistat|motab3a|motaba3a|rebawnd|tlatyat|talatyat)\b",
    r"\b(?:de2ay2|deqayeq|daqayeq|yestahal|yestahel|edelo|adeelo|y2ta3|ye2ta3|khatf|y5tf)\b",
    r"\b\w*[2375]\w*\b",
)


def detect_user_language(text: str | None) -> str:
    value = text or ""
    if contains_arabic(value):
        return "arabic"
    lowered = value.lower()
    if any(re.search(pattern, lowered) for pattern in _FRANKO_HINTS):
        return "franko"
    return "english"


def response_language_instruction(question: str | None) -> str:
    language = detect_user_language(question)
    if language == "arabic":
        return (
            "Match the coach's language. Answer in Arabic. If the question is Egyptian "
            "slang, use natural Egyptian Arabic; if it is formal Arabic, use clear "
            "standard Arabic. Keep player names, team codes, numbers, and source names unchanged."
        )
    if language == "franko":
        return (
            "Match the coach's language. Answer in Franko/Arabizi Egyptian Arabic "
            "using Latin letters and common numbers like 2, 3, 5, and 7 when natural. "
            "Keep player names, team codes, numbers, and source names unchanged."
        )
    return "Answer in English."


def classifier_language_instruction() -> str:
    return (
        "The coach may ask in English, Arabic, Egyptian Arabic slang, or Franko/Arabizi "
        "(Egyptian Arabic written with Latin letters/numbers). Interpret all of them, "
        "but return the JSON schema labels, routes, metrics, and recipe names in English. "
        "Common mappings: افضل/احسن/a7san/afdal -> best/top; اكتر/aktar/aktr -> most; "
        "نقط/no2at -> points; اسيست/asist/assistat -> assists; متابعات/motab3a/rebawnd -> rebounds; "
        "قطع/خطف/y2ta3/khatf/y5tf -> steals; بلوك/sad/blockat -> blocks; "
        "تلاتيات/talatayat/tlatyat -> three_point_shooting; ثنائيات -> two_point_shooting; "
        "رميات حره/7orra -> free_throw_percentage; دقايق/de2ay2 -> minutes; "
        "تشكيله/tashkila/lineup/squad -> squad or lineup; مصر/masr/msr -> EGY."
    )


_QUERY_ALIASES: tuple[tuple[str, tuple[str, ...]], ...] = (
    ("top best highest rank leaders", (r"افضل", r"احسن", r"اعلي", r"اكثر", r"اكتر", r"\b(?:a7san|ahsan|afdal|a3la|aktar|aktr)\b")),
    ("most who has", (r"مين", r"من هو", r"\b(?:meen|men)\b")),
    ("compare comparison versus vs who is better between", (r"قارن", r"مقارنه", r"مقارنة", r"مين احسن", r"\b(?:qarin|karen|moqarna|mokarna|compare|vs)\b")),
    ("points scorers scoring scored pts", (r"نقط", r"نقاط", r"هداف", r"تسجيل", r"سكور", r"\b(?:no2at|nokat|nuqat|points?|pts|scorers?|score|scoring)\b")),
    ("assists assisters asts playmakers", (r"اسيست", r"اسست", r"تمريرات حاسمه", r"تمريره حاسمه", r"صناع لعب", r"\b(?:asist|assistat|assists?|asts?|playmakers?)\b")),
    ("steals stealers stl steels", (r"ستيل", r"سرقات", r"\b(?:steals?|stealers?|stls?|steels?)\b")),
    ("steal the ball take the ball away defensive pressure ball pressure", (r"قطع", r"يقطع", r"خطف", r"يخطف", r"ضغط علي الكوره", r"ضغط عالكوره", r"\b(?:y2ta3\w*|ye2ta3\w*|khatf\w*|y5tf\w*|edghat\w*|daght)\b")),
    ("blocks blk block", (r"بلوك", r"بلوكات", r"صد", r"تصدي", r"\b(?:blocks?|blockat|blk|sadat?)\b")),
    ("turnovers tov giveaways", (r"تيرن اوفر", r"فقدان", r"ضيعت الكوره", r"\b(?:turnovers?|tov|giveaways?)\b")),
    ("defensive rebounds defensive rebounders defensive boards dreb", (r"دفاعيه.*(?:ريباوند|متابعات)", r"(?:ريباوند|متابعات).*دفاعيه", r"\b(?:dreb|defensive reb|def reb)\b")),
    ("offensive rebounds offensive rebounders offensive boards oreb", (r"هجوميه.*(?:ريباوند|متابعات)", r"(?:ريباوند|متابعات).*هجوميه", r"\b(?:oreb|offensive reb|off reb)\b")),
    ("rebounds rebounders reb boards", (r"ريباوند", r"متابعات", r"متابعه", r"\b(?:rebounds?|rebounders?|rebs?|boards?|motab3a|motaba3a|rebawnd)\b")),
    ("minutes mins playing time", (r"دقايق", r"دقائق", r"وقت لعب", r"\b(?:de2ay2|deqayeq|daqayeq|minutes?|mins?)\b")),
    ("plus minus plus/minus +/-", (r"بلس ماينس", r"بلس/ماينس", r"\bplus\s*minus\b", r"\+/-")),
    ("efficiency eff", (r"كفاءه", r"كفاءة", r"فاعليه", r"فاعلية", r"\b(?:efficiency|efficient|eff)\b")),
    ("three point 3pt 3 pointers threes", (r"تلاتيات", r"ثلاثيات", r"تصويب ثلاثي", r"\b(?:3\s*pt|3\s*pts|3\s*pointers?|three[-\s]?point|threes|tlatyat|talatyat|talatat)\b")),
    ("two point 2pt 2 pointers", (r"ثنائيات", r"تصويب ثنائي", r"\b(?:2\s*pt|2\s*pts|2\s*pointers?|two[-\s]?point)\b")),
    ("free throw free throws ft foul shots", (r"رميات حره", r"رميات حرة", r"رميه حره", r"رميه حرة", r"\b(?:free throws?|ft|7orra|horra|ramyat 7orra)\b")),
    ("squad lineup starting five best squad who should start", (r"تشكيله", r"تشكيلة", r"خماسي", r"مين يبدا", r"مين يبدأ", r"\b(?:squad|lineup|line-up|tashkila|tashkeela|starting five|who should start)\b")),
    ("deserve more minutes should get more minutes underused more playing time", (r"يستاهل.*دقايق", r"ياخد.*دقايق", r"اديله.*دقايق", r"فرص اكتر", r"\b(?:yestahal|yestahel|yakhd|edelo|adeelo)\b")),
    ("game match", (r"ماتش", r"مباراه", r"مباراة", r"لقاء", r"\b(?:match|matsh|game|fixture)\b")),
    ("result final score did we win lose beat", (r"نتيجه", r"نتيجة", r"كسبنا", r"خسرنا", r"السكور", r"\b(?:result|score|win|lose|beat|kasabna|kheserna|5serna)\b")),
    ("make write create generate give me", (r"اكتب", r"اعمل", r"طلع", r"جهز", r"\b(?:write|make|create|generate|prepare|e3mel|ektb|etb)\b")),
    ("report summary summarize recap", (r"تقرير", r"لخص", r"ملخص", r"تحليل", r"\b(?:report|summary|summarize|summarise|recap|taqrir|la5as|lakhas|molakhas|tahleel)\b")),
    ("schedule fixtures calendar when next game next match", (r"جدول", r"مواعيد", r"امتي", r"امتى", r"الماتش الجاي", r"\b(?:schedule|fixtures?|calendar|emta|emty|next match|next game)\b")),
    ("injury injuries injured who is out sidelined recovery", (r"اصابه", r"اصابات", r"مصاب", r"مين بره", r"هيرجع امتي", r"\b(?:injur(?:y|ies|ed)|mesab|esaba|who is out|recovery)\b")),
    ("height weight tallest shortest heaviest lightest", (r"طول", r"وزن", r"اطول", r"اقصر", r"اتقل", r"\b(?:height|weight|tallest|shortest|heaviest|lightest|tool|wazn)\b")),
    ("plan improve better optimize", (r"خطه", r"خطة", r"حسن", r"طور", r"\b(?:plan|improve|better|optimi[sz]e|khetta|5etta)\b")),
    ("egypt egy", (r"مصر", r"منتخب مصر", r"\b(?:egypt|egy|masr|msr)\b")),
    ("angola ang", (r"انجولا", r"أنجولا", r"\b(?:angola|ang)\b")),
    ("mali mli", (r"مالي", r"\b(?:mali|mli)\b")),
    ("uganda uga", (r"اوغندا", r"أوغندا", r"يوغندا", r"\b(?:uganda|uga)\b")),
)


def expand_query_for_matching(text: str | None) -> str:
    """Append English intent aliases for Arabic and Franko matching.

    Existing parsers are regex-based and mostly English. This keeps those regexes as
    the source of truth while making multilingual coach phrasing hit the same paths.
    """
    raw = text or ""
    normalized = normalize_arabic(raw)
    lowered = raw.translate(_ARABIC_DIGITS).lower()
    aliases: list[str] = []
    search_space = f"{lowered} {normalized}"
    for alias, patterns in _QUERY_ALIASES:
        if any(re.search(pattern, search_space) for pattern in patterns):
            aliases.append(alias)

    top_match = re.search(
        r"(?:افضل|احسن|اعلي|اكثر|اكتر|top|best|a7san|ahsan|afdal|a3la|aktar|aktr)\s+(\d{1,2})(?![a-z])",
        search_space,
    )
    if top_match:
        aliases.append(f"top {top_match.group(1)}")
    player_count_match = re.search(r"(\d{1,2})(?![a-z])\s+(?:لاعيب|لعيب|لعيبه|لاعب|players?|la3eb|la3eba)", search_space)
    if player_count_match:
        aliases.append(f"{player_count_match.group(1)} players")

    return " ".join(part for part in [lowered, normalized, *aliases] if part).strip()
