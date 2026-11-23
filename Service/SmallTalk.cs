using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Small-talk layer that sits in front of the RAG pipeline.
/// Returns true + a canned reply for chit-chat; false means "send to RAG".
/// Requires .NET 6+ (Random.Shared, records).
/// </summary>
public static class SmallTalk
{
    private const int MaxChars = 150;
    private const RegexOptions Opt = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private readonly record struct Ctx(string Plant, string PlantSuffix, DateTime Now);
    private sealed record Rule(string Name, Regex Pattern, int MaxWords, Func<Ctx, string> Reply);

    // ------------------------------------------------------------------
    // Public entry point (same signature as before)
    // ------------------------------------------------------------------
    public static bool TryGetSmallTalkReply(string text, string plant, out string reply)
    {
        reply = "";

        // 1. Small talk is short.
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxChars)
            return false;

        var t = Normalize(text);
        if (t.Length == 0) return false;

        // 2. Safeguard: anything that smells like a policy/HR question goes to RAG.
        if (PolicyGuard.IsMatch(t))
            return false;

        int words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        var ctx = new Ctx(
            plant ?? "",
            string.IsNullOrWhiteSpace(plant) ? "" : $" for the {plant} plant",
            LocalNow());

        // 3. First matching rule wins (order matters).
        foreach (var rule in Rules)
        {
            if (words <= rule.MaxWords && rule.Pattern.IsMatch(t))
            {
                reply = rule.Reply(ctx);
                return true;
            }
        }

        return false; // let the RAG pipeline handle it
    }

    // ------------------------------------------------------------------
    // Policy / HR safeguard (whole-word matches only)
    // ------------------------------------------------------------------
    private static readonly Regex PolicyGuard = new(
        @"\b(policy|policies|leave|leaves|salary|salaries|pay|payslip|payroll|travel|loan|loans|benefit|benefits|hr|manager|allowance|allowances|resign|resignation|notice|claim|claims|reimburse|reimbursement|insurance|medical|bonus|increment|appraisal|gratuity|pf|esi|esic|overtime|attendance|shift|holiday|holidays|probation|joining|termination|transfer|relocation|form|forms|annexure|compliance|grade|ctc|deduction|tax|uniform|canteen|safety|harassment|grievance|code of conduct)\b",
        Opt);

    // ------------------------------------------------------------------
    // Rules
    // ------------------------------------------------------------------
    private static readonly Rule[] Rules =
    {
        // ---- Date & time ----
        R("datetime", @"\b(date and time|time and date)\b", 10,
            c => $"It's {Fmt(c.Now, "dddd, MMMM d, yyyy")}, {Fmt(c.Now, "h:mm tt")}."),

        R("date", @"\b(what(s| is) (the )?(todays? )?(date|day)|todays date|what day (is it|is today)|which day is it|current date)\b", 10,
            c => $"Today is {Fmt(c.Now, "dddd, MMMM d, yyyy")}."),

        R("time", @"\b(what(s| is) the (current )?time|what time is it|current time|time now|tell me the time)\b", 10,
            c => $"The current time is {Fmt(c.Now, "h:mm tt")}."),

        // ---- Goodbyes (before thanks so "thanks, bye" ends politely) ----
        R("bye", @"\b(bye+|goodbye|see you|see ya|good night|take care|talk (to you )?later|catch you later|have a (good|great|nice) (day|evening|weekend))\b", 8,
            _ => Pick(
                "Goodbye! Come back any time you have a question about MEAI policies.",
                "Take care! I'm here whenever you need to check a policy.",
                "See you later! Feel free to return with any policy questions.")),

        // ---- Thanks ----
        R("thanks", @"\b(thank you|thanks+|thx|thank u|ty|many thanks|much appreciated|appreciate (it|that|your help)|cheers)\b", 10,
            _ => Pick(
                "You're welcome! Let me know if there's anything else you'd like to check in the policies.",
                "Happy to help! Ask me anything else about company policies.",
                "Anytime! Reach out if you have more policy questions.")),

        // ---- Apologies ----
        R("sorry", @"\b(sorry|my bad|apologies|pardon me)\b", 6,
            _ => "No need to apologise! What would you like to know about our policies?"),

        // ---- Festivals / wishes (useful for an India-based plant) ----
        R("wishes", @"\b(happy (diwali|deepavali|holi|new year|navratri|dussehra|eid|birthday|pongal|onam|christmas|republic day|independence day|raksha bandhan|makar sankranti|uttarayan)|eid mubarak|merry christmas|season s greetings|diwali wishes)\b", 8,
            _ => "Thank you, and the same to you! 🎉 Is there anything I can help you look up in the policies?"),

        // ---- Identity & capabilities ----
        R("name", @"\b(whats your name|what is your name|what should i call you|your name)\b", 8,
            c => $"I'm the MEAI Assistant{c.PlantSuffix}. How can I help you today?"),

        R("identity", @"\b(who are you|what are you|introduce yourself|tell me about yourself)\b", 8,
            c => $"I'm the MEAI Assistant{c.PlantSuffix}. I can answer questions about company policies (leave, travel, loans, benefits, compliance and more), explain forms and annexures, and help with calculations based on policy rules. What would you like to know?"),

        R("capabilities", @"\b(what can you do|how can you help|how do you work|what do you know|what are your (features|capabilities)|what can i ask)\b", 10,
            _ => "You can ask me about MEAI policies such as leave, travel, loans, benefits and compliance. I can also explain forms and annexures and help with calculations based on policy rules. Just type your question!"),

        R("help", @"^(help|help me|i need help|need help|can you help|can you help me|please help|please help me)$", 6,
            _ => "Of course! Tell me what you'd like to know, for example 'How many casual leaves do I get?' or 'What is the travel allowance policy?'"),

        R("creator", @"\b(who (made|created|built|programmed|developed|designed) you|who is your (creator|developer|owner))\b", 10,
            _ => "I was developed by the internal systems team to help employees quickly find answers in our HR and company policy documents."),

        R("ai", @"\b(are you (a )?(robot|bot|human|ai|real|person|chatgpt|gpt)|am i (talking|speaking|chatting) (to|with) (a )?(human|person|bot|robot))\b", 10,
            _ => "I am an AI assistant, here to help you navigate our company policies and guidelines quickly and accurately."),

        R("presence", @"^(are you (there|online|working|awake|alive)|anyone (there|here)|hello are you there|test|testing|ping)$", 6,
            _ => "Yes, I'm here! How can I help you with MEAI policies today?"),

        // ---- Personal questions ----
        R("personal", @"\b(how old are you|whats your age|where (do you live|are you from)|do you (sleep|eat)|are you (married|single)|do you have (a )?(family|friends|feelings))\b", 10,
            _ => "I'm a virtual assistant, so none of that applies to me! I'm here for your policy questions, though. What can I look up?"),

        R("favorites", @"\b(do you like|what is your favou?rite|who is your favou?rite|whats your favou?rite)\b", 10,
            _ => "I don't have personal preferences, but I do love a well-written HR policy! How can I assist you today?"),

        // ---- How are you / meet ----
        R("howareyou", @"\b(how are you|how r u|how are u|how is it going|hows it going|hows everything|how do you do|whats up|wassup|sup|how have you been|hows your day)\b", 10,
            _ => Pick(
                "I'm doing well, thanks for asking! How can I help you today? You can ask me about any company policy.",
                "All good here! What can I help you find in the policies today?")),

        R("nicemeet", @"\b(nice|pleased|good|great) to (meet|see) you\b", 8,
            _ => "Nice to meet you too! What would you like to know about MEAI policies?"),

        // ---- Fun ----
        R("joke", @"\b(tell me a joke|make me laugh|know any jokes|another joke|one more joke|say something funny|funny joke|joke)\b", 8,
            _ => Pick(
                "Why did the employee bring a ladder to work? Because they wanted to climb the corporate ladder! ...But seriously, how can I help you with our policies today?",
                "Why don't managers ever get lost? They always follow the org chart! Now, what can I look up for you?",
                "I tried to write a joke about leave policies, but I couldn't find the right time off for it. 😄 What can I help with?",
                "What's an HR assistant's favourite exercise? Policy stretches! Anything I can look up for you?")),

        R("weather", @"\b(hows the weather|how is the weather|weather today|is it (raining|hot|cold)|weather)\b", 8,
            _ => "I don't have a window, but I hope it's nice outside! I'm here to help you with company policies. What can I look up for you?"),

        R("bored", @"\b(im bored|i am bored|entertain me|say something interesting)\b", 8,
            _ => "Here's a fun one: reading policy documents isn't most people's idea of fun, which is why I exist! Ask me anything and I'll dig through them for you."),

        // ---- Feedback ----
        R("compliment", @"\b(good (bot|job|work|answer)|well done|nice work|great job|(you are|youre|u r) (great|awesome|smart|helpful|amazing|good|the best)|that was helpful|very helpful|so helpful|love you|you rock)\b", 10,
            _ => Pick(
                "Thank you, that's kind of you to say! Let me know if you need anything else.",
                "Glad I could help! Ask away if you have more questions.")),

        R("negative", @"\b(stupid|useless|dumb|idiot|worst|hate you|you suck|not helpful|thats wrong|wrong answer|you are wrong|youre wrong|incorrect)\b", 10,
            _ => "I'm sorry I wasn't helpful. Could you rephrase your question or add a few more details? For anything official, please confirm with the HR team as well."),

        // ---- Acknowledgements (strict, whole message only) ----
        R("ack", @"^(ok+|okay+|k|kk|cool+|great|awesome|perfect|fine|alright|sure|nice|good|sounds good|got it|understood|makes sense|i see|noted|will do|right|oh ok|oh okay|ok cool|ok great|ok got it|ok thanks)$", 4,
            _ => Pick(
                "Great! Let me know if you need help with anything else.",
                "Sounds good! I'm here if you have more questions.")),

        // ---- Greetings (last: catch-all, must start the message) ----
        R("greeting", @"^(hi+|hii+|hello+|hey+|heyy+|hola|namaste|namaskar|kem cho|greetings|howdy|yo|hi there|hello there|hey there|good (morning|afternoon|evening)|gm|ge)\b", 8,
            c => Greeting(c)),
    };

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private static Rule R(string name, string pattern, int maxWords, Func<Ctx, string> reply)
        => new(name, new Regex(pattern, Opt), maxWords, reply);

    private static string Pick(params string[] options)
        => options[Random.Shared.Next(options.Length)];

    private static string Fmt(DateTime dt, string format)
        => dt.ToString(format, CultureInfo.InvariantCulture);

    private static string Greeting(Ctx c)
    {
        string part = c.Now.Hour switch
        {
            < 12 => "Good morning",
            < 17 => "Good afternoon",
            _ => "Good evening"
        };

        return Pick(
            $"{part}! How can I help you today? You can ask me about any company policy.",
            $"Hello! Welcome to the MEAI Assistant{c.PlantSuffix}. What can I help you with?",
            "Hi there! Ask me anything about MEAI policies, forms or benefits.");
    }

    /// <summary>
    /// Lowercase, strip apostrophes/punctuation, squash "heyyyy" to "heyy", collapse spaces.
    /// "What's the date?" -> "whats the date"
    /// </summary>
    private static string Normalize(string s)
    {
        s = s.ToLowerInvariant().Replace("\u2019", "").Replace("'", "");
        s = Regex.Replace(s, @"[^\p{L}\p{N}\s]", " ");
        s = Regex.Replace(s, @"(\p{L})\1{2,}", "$1$1");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>Current time in the plant's timezone (IST), regardless of server timezone.</summary>
    private static DateTime LocalNow()
    {
        foreach (var id in new[] { "India Standard Time", "Asia/Kolkata" }) // Windows, Linux
        {
            try
            {
                return TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(id));
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return DateTime.Now;
    }
}