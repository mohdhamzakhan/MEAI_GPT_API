using static MEAI_GPT_API.Models.Conversation;

namespace MEAI_GPT_API.Service.Models
{
    public class ConversationAnalysisService
    {
        private readonly ILogger<ConversationAnalysisService> _logger;
        private readonly PolicyAnalysisService _policyAnalysis;
        public ConversationAnalysisService( 
            ILogger<ConversationAnalysisService> logger,
            PolicyAnalysisService policyAnalysis)
        {
            _logger = logger;
            _policyAnalysis = policyAnalysis;
        }

        public List<string> ExtractKeyTopics(string text)
        {
            var lowerText = text.ToLowerInvariant();
            var topics = new HashSet<string>();

            // Common topic indicators - domain nouns
            string[] topicKeywords = new[]
            {
        // General categories
        "names", "suggestions", "options", "ideas", "list", "examples", "types", "kinds",
        "methods", "ways", "approaches", "solutions", "strategies", "techniques", "tips",
        "advice", "recommendations", "guidelines", "rules", "policies", "procedures",
        "steps", "process", "information", "details", "facts", "data", "statistics",
        
        // Specific domains
        "leave", "policy", "salary", "benefits", "training", "attendance", "performance",
        "food", "recipe", "cooking", "health", "exercise", "medicine", "travel",
        "places", "locations", "books", "movies", "music", "technology", "software",
        "business", "career", "education", "learning", "courses", "skills"
    };

            // Extract topic keywords present in the text
            foreach (var keyword in topicKeywords)
            {
                if (lowerText.Contains(keyword))
                {
                    topics.Add(keyword);
                }
            }

            // Extract potential nouns (simple approach)
            var words = lowerText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                // Add words that are likely to be topic nouns (length > 3, not common words)
                if (word.Length > 3 && !TextUtils.IsCommonWord(word))
                {
                    topics.Add(word);
                }
            }

            return topics.ToList();
        }      
        public string ResolvePronouns(string question, ConversationContext context)
        {
            if (context.NamedEntities.Count == 0) return question;

            string lastEntity = context.NamedEntities.Last();

            question = question.Replace("her", lastEntity, StringComparison.OrdinalIgnoreCase);
            question = question.Replace("his", lastEntity, StringComparison.OrdinalIgnoreCase);
            question = question.Replace("their", lastEntity, StringComparison.OrdinalIgnoreCase);
            return question;
        }
        public string BuildContextualQuery(string currentQuestion, List<ConversationTurn> history)
        {
            if (history.Count == 0) return currentQuestion;

            var contextualPhrases = new[]
{
    // Pronouns
    "he", "she", "him", "her", "they", "them", "his", "hers", "their", "theirs", "it", "its",

    // Demonstratives
    "this", "that", "those", "these",

    // Follow-up / connective words
    "also", "and", "but", "or", "then", "next", "after", "before", "furthermore", "moreover", "besides",

    // Question prompts
    "what about", "who else", "anything else", "how about", "can i also", "does it mean", "in that case",
    "how?", "why?", "when?", "where?", "what if?", "which one?", "how many?", "how long?", "what now?",

    // Anaphoric phrases
    "same", "as before", "previous one", "last one", "mentioned", "earlier", "above", "following that",
    "that one", "the same", "that case", "it again", "same thing", "another one", "one more",

    // Roles or objects
    "the person", "the policy", "the rule", "the regulation", "the requirement", "the document", "the clause",

    // Quantifiers
    "some", "any", "all", "none", "more", "less", "other", "another", "rest",

    // Clarifiers / corrections
    "not that", "actually", "i meant", "no, i mean", "what i meant was"
};

            if (contextualPhrases.Any(phrase => currentQuestion.ToLower().Contains(phrase)))
            {
                var lastTurn = history.LastOrDefault();
                if (lastTurn != null)
                {
                    return $"Previous context: {lastTurn.Question} -> {lastTurn.Answer} Current question: {currentQuestion}";
                }
            }

            return currentQuestion;
        }
        public bool IsTopicChanged(string question, ConversationContext context)
        {
            if (string.IsNullOrWhiteSpace(question)) return true;

            var lowerQuestion = question.ToLowerInvariant();

            // 🆕 ADD THIS AT THE BEGINNING - Generic section change detection
            if (_policyAnalysis.HasSectionReference(lowerQuestion))
            {
                var currentSection = _policyAnalysis.ExtractSectionReference(lowerQuestion);
                if (context.History.Any())
                {
                    var lastQuestion = context.History.Last().Question.ToLowerInvariant();
                    if (_policyAnalysis.HasSectionReference(lastQuestion))
                    {
                        var lastSection = _policyAnalysis.ExtractSectionReference(lastQuestion);
                        if (currentSection != lastSection)
                        {
                            _logger.LogDebug($"🚫 Section change detected: {lastSection} → {currentSection}");
                            return true;
                        }
                    }
                }
            }
            // 1. Universal continuation indicators
            string[] universalContinuation = new[]
            {
        // Direct continuation requests
        "more", "other", "different", "additional", "extra", "another", "else",
        "further", "continue", "next", "also", "too", "as well", "besides",
        
        // Modification requests  
        "but", "however", "though", "although", "instead", "rather", "better",
        "alternative", "similar", "like", "unlike", "compared", "versus",
        
        // Expansion requests
        "tell me more", "give me more", "show me more", "any other", "what other",
        "can you", "could you", "would you", "please", "help me", "suggest",
        
        // Clarification/Follow-up
        "what about", "how about", "what if", "suppose", "assuming", "given",
        "in case", "regarding", "concerning", "about", "related", "same",
        
        // Pronouns (strong continuation indicators)
        "it", "this", "that", "these", "those", "they", "them", "he", "she",
        "his", "her", "their", "its"
    };

            // 2. Check for universal continuation phrases
            if (universalContinuation.Any(phrase => lowerQuestion.Contains(phrase)))
            {
                _logger.LogDebug($"Universal continuation detected: {question}");
                return false;
            }

            // 3. Smart topic overlap detection
            if (context.History.Any())
            {
                var currentTopics = ExtractKeyTopics(question);
                var lastQuestion = context.History.Last().Question;
                var lastTopics = ExtractKeyTopics(lastQuestion);

                // Calculate topic overlap
                var commonTopics = currentTopics.Intersect(lastTopics, StringComparer.OrdinalIgnoreCase).ToList();
                var overlapRatio = commonTopics.Count > 0 ?
                    (double)commonTopics.Count / Math.Max(currentTopics.Count, lastTopics.Count) : 0;

                if (overlapRatio >= 0.3) // 30% topic overlap indicates same domain
                {
                    _logger.LogDebug($"Topic overlap detected ({overlapRatio:P0}): {string.Join(", ", commonTopics)}");
                    return false;
                }
            }

            // 4. Semantic similarity with conversation history
            if (context.History.Count > 0)
            {
                var recentQuestions = context.History.TakeLast(3).Select(h => h.Question).ToList();

                foreach (var recentQ in recentQuestions)
                {
                    double similarity = TextUtils.CalculateAdvancedSimilarity(question, recentQ);
                    if (similarity >= 0.25) // Lower threshold for better continuity
                    {
                        _logger.LogDebug($"Semantic similarity detected ({similarity:P0}) with: {recentQ}");
                        return false;
                    }
                }
            }

            // 5. Pattern-based continuation detection
            if (IsQuestionPatternContinuation(question, context))
            {
                return false;
            }

            // 6. Use topic anchor (last resort)
            var anchor = context.LastTopicAnchor ?? "";
            if (!string.IsNullOrWhiteSpace(anchor))
            {
                double anchorSim = TextUtils.CalculateAdvancedSimilarity(question, anchor);
                if (anchorSim >= 0.2)
                {
                    _logger.LogDebug($"Topic anchor similarity ({anchorSim:P0}): {anchor}");
                    return false;
                }
            }

            // 7. Default: Topic changed
            _logger.LogDebug($"Topic change detected for: {question}");
            return true;
        }
        public bool IsTopicChangedLightweight(string question, ConversationContext context)
        {
            if (!context.History.Any()) return false;

            var lowerQuestion = question.ToLowerInvariant();

            // Quick continuation check
            string[] quickContinuation = { "more", "other", "different", "also", "and", "but" };
            if (quickContinuation.Any(word => lowerQuestion.Contains(word)))
                return false;

            // Simple word overlap with last question
            if (context.History.Any())
            {
                var lastQ = context.History.Last().Question.ToLowerInvariant();
                var words1 = question.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var words2 = lastQ.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var overlap = words1.Intersect(words2, StringComparer.OrdinalIgnoreCase).Count();

                if (overlap >= 2) return false; // At least 2 common words = same topic
            }

            return true; // Default to changed for general queries
        }
        public bool IsFollowUpQuestion(string question, ConversationContext context)
        {
            var followUpIndicators = new[]
            {
        "what about", "how about", "also", "and", "additionally", "furthermore",
        "he", "she", "it", "they", "this", "that", "same", "similar",
        "phir", "aur", "bhi", "uske", "uska", "iske", "agar"
    };

            var lowerQuestion = question.ToLowerInvariant();
            return followUpIndicators.Any(indicator => lowerQuestion.Contains(indicator));
        }
        public bool IsQuestionPatternContinuation(string question, ConversationContext context)
        {
            if (!context.History.Any()) return false;

            var lowerQ = question.ToLowerInvariant().Trim();

            // Patterns that usually indicate continuation
            string[] continuationPatterns = new[]
            {
        // Request patterns
        @"^(can|could|would|will) you (tell|give|show|suggest|recommend)",
        @"^(tell|give|show|suggest) me (more|other|different|some)",
        @"^(what|which) (other|else|more|about)",
        @"^(any|some) (other|more|different)",
        
        // Comparative patterns  
        @"^(but|however|instead|rather) ",
        @"little (different|more)",
        @"bit (different|more)",
        @"something (else|different|more)",
        
        // Follow-up patterns
        @"^(also|too|as well)",
        @"^(and|plus|additionally)",
        @"^(or|maybe|perhaps)",
    };

            foreach (var pattern in continuationPatterns)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(lowerQ, pattern))
                {
                    _logger.LogDebug($"Continuation pattern matched: {pattern}");
                    return true;
                }
            }

            return false;
        }
        public string DetermineTopicTag(string question, string answer)
        {
            var lowerQuestion = question.ToLowerInvariant();
            var lowerAnswer = answer.ToLowerInvariant();
            var combinedText = $"{lowerQuestion} {lowerAnswer}";

            // HR Policy topic mapping
            var topicKeywords = new Dictionary<string, string[]>
            {
                ["leave_policy"] = new[] { "leave", "cl", "sl", "casual", "sick", "pto", "vacation", "holiday", "absence" },
                ["attendance"] = new[] { "attendance", "punctuality", "working hours", "shift", "late", "early" },
                ["payroll"] = new[] { "salary", "pay", "payroll", "bonus", "increment", "deduction", "tax" },
                ["benefits"] = new[] { "insurance", "medical", "health", "benefits", "reimbursement", "allowance" },
                ["performance"] = new[] { "appraisal", "performance", "review", "rating", "feedback", "kpi" },
                ["grievance"] = new[] { "complaint", "grievance", "issue", "problem", "dispute", "conflict" },
                ["training"] = new[] { "training", "development", "course", "certification", "skill", "learning" },
                ["policy_general"] = new[] { "policy", "rule", "regulation", "procedure", "guideline", "compliance" }
            };

            foreach (var topic in topicKeywords)
            {
                if (topic.Value.Any(keyword => combinedText.Contains(keyword)))
                {
                    return topic.Key;
                }
            }

            return "general";
        }

    }
}
