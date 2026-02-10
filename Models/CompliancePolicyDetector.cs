// COMPREHENSIVE DOCUMENT TYPE DETECTION FOR ALL COMPLIANCE POLICIES

using System.Text.RegularExpressions;

namespace MEAI_GPT_API.Service.Models
{
    public class CompliancePolicyDetector
    {
        // Document type patterns with their keywords
        private readonly Dictionary<string, DocumentTypePattern> _policyPatterns = new()
        {
            // 1. Company Ethics
            ["Company Ethics"] = new()
            {
                PrimaryKeywords = new[] { "company ethics", "code of conduct", "ethics policy", "ethical conduct", "integrity" },
                SecondaryKeywords = new[] { "conduct", "ethics", "values", "principles", "behavior", "moral" },
                Category = "Ethics & Conduct"
            },

            // 2. ISMS General
            ["ISMS General"] = new()
            {
                PrimaryKeywords = new[] { "isms general", "information security management", "isms policy" },
                SecondaryKeywords = new[] { "governance", "framework", "strategy", "awareness", "training",
                    "organization", "roles", "responsibilities", "committee", "management", "overview" },
                Category = "Information Security",
                ExcludeKeywords = new[] { "technical", "implementation", "network", "firewall", "encryption" }
            },

            // 3. ISMS Technical
            ["ISMS Technical"] = new()
            {
                PrimaryKeywords = new[] { "isms technical", "technical implementation", "technical controls" },
                SecondaryKeywords = new[] { "technical", "network", "firewall", "encryption", "authentication",
                    "database", "server", "application", "vulnerability", "penetration", "monitoring",
                    "logging", "backup", "recovery", "patch", "infrastructure" },
                Category = "Information Security"
            },

            // 4. Anti-Trust / Anti-Monopoly
            ["Anti-Trust Policy"] = new()
            {
                PrimaryKeywords = new[] { "anti-trust", "anti trust", "antitrust", "anti-monopoly", "antimonopoly", "competition law" },
                SecondaryKeywords = new[] { "monopoly", "cartel", "price fixing", "market manipulation",
                    "competition", "fair trade", "restraint of trade", "collusion" },
                Category = "Legal Compliance"
            },

            // 5. Graphic Standards
            ["Graphic Standards Policy"] = new()
            {
                PrimaryKeywords = new[] { "graphic standards", "brand guidelines", "visual identity", "logo usage" },
                SecondaryKeywords = new[] { "graphics", "branding", "logo", "colors", "fonts", "typography",
                    "design", "visual", "brand identity", "style guide" },
                Category = "Brand & Marketing"
            },

            // 6. Whistle Blower
            ["Whistle Blower Policy"] = new()
            {
                PrimaryKeywords = new[] { "whistle blower", "whistleblower", "whistle-blower", "reporting mechanism" },
                SecondaryKeywords = new[] { "reporting", "complaint", "grievance", "anonymous", "protection",
                    "retaliation", "confidential", "hotline", "speak up" },
                Category = "Ethics & Conduct"
            },

            // 7. Social Risk
            ["Social Risk Policy"] = new()
            {
                PrimaryKeywords = new[] { "social risk", "social responsibility", "community impact" },
                SecondaryKeywords = new[] { "social", "community", "stakeholder", "impact assessment",
                    "social compliance", "labor practices", "working conditions" },
                Category = "ESG & Sustainability"
            },

            // 8. Conflict of Interest
            ["Conflict of Interest Policy"] = new()
            {
                PrimaryKeywords = new[] { "conflict of interest", "conflict-of-interest", "coi policy" },
                SecondaryKeywords = new[] { "conflict", "interest", "disclosure", "personal interest",
                    "related party", "independence", "impartiality", "bias" },
                Category = "Ethics & Conduct"
            },

            // 9. Export Management & Compliance
            ["Export Compliance Policy"] = new()
            {
                PrimaryKeywords = new[] { "export management", "export compliance", "export control", "trade compliance" },
                SecondaryKeywords = new[] { "export", "import", "customs", "trade", "sanctions", "embargo",
                    "dual-use", "restricted party", "export license" },
                Category = "Legal Compliance"
            },

            // 10. Litigation
            ["Litigation Policy"] = new()
            {
                PrimaryKeywords = new[] { "litigation policy", "litigation management", "legal disputes" },
                SecondaryKeywords = new[] { "litigation", "lawsuit", "legal action", "dispute resolution",
                    "court", "arbitration", "settlement", "legal proceedings" },
                Category = "Legal Compliance"
            },

            // 11. Prevention of Sexual Harassment
            ["Sexual Harassment Prevention Policy"] = new()
            {
                PrimaryKeywords = new[] { "sexual harassment", "sexual harrassment", "posh", "prevention of sexual harassment" },
                SecondaryKeywords = new[] { "harassment", "sexual", "misconduct", "unwelcome", "workplace safety",
                    "gender", "discrimination", "complaint mechanism", "internal committee" },
                Category = "HR & Workplace"
            },

            // 12. Centralized Compliance Program
            ["Centralized Compliance Program"] = new()
            {
                PrimaryKeywords = new[] { "centralized compliance", "compliance program", "compliance management system" },
                SecondaryKeywords = new[] { "compliance", "program", "centralized", "governance", "oversight",
                    "compliance officer", "monitoring", "audit", "risk assessment" },
                Category = "Governance & Compliance"
            },

            // 13. Anti-Bribery
            ["Anti-Bribery Policy"] = new()
            {
                PrimaryKeywords = new[] { "anti-bribery", "anti bribery", "antibribery", "anti-corruption", "fcpa", "ukba" },
                SecondaryKeywords = new[] { "bribery", "corruption", "kickback", "facilitation payment",
                    "gift", "hospitality", "third party", "due diligence" },
                Category = "Ethics & Conduct"
            },

            // 14. CSR
            ["CSR Policy"] = new()
            {
                PrimaryKeywords = new[] { "csr policy", "corporate social responsibility", "csr" },
                SecondaryKeywords = new[] { "social responsibility", "community", "philanthropy", "sustainability",
                    "stakeholder", "environment", "society", "giving back" },
                Category = "ESG & Sustainability"
            },

            // 15. Reporting Illegal & Misconduct
            ["Misconduct Reporting Policy"] = new()
            {
                PrimaryKeywords = new[] { "reporting illegal", "misconduct incidents", "incident reporting", "ethics reporting" },
                SecondaryKeywords = new[] { "misconduct", "illegal", "violation", "reporting", "incident",
                    "fraud", "unethical", "non-compliance", "breach" },
                Category = "Ethics & Conduct"
            },

            // 16. Human Rights
            ["Human Rights Policy"] = new()
            {
                PrimaryKeywords = new[] { "human rights", "human rights policy", "rights and dignity" },
                SecondaryKeywords = new[] { "human rights", "dignity", "labor rights", "freedom", "equality",
                    "non-discrimination", "fair treatment", "working conditions", "child labor", "forced labor" },
                Category = "ESG & Sustainability"
            },

            // 17. Personal Data Protection
            ["Personal Data Protection Policy"] = new()
            {
                PrimaryKeywords = new[] { "personal data protection", "data privacy", "gdpr", "pdpa", "privacy policy" },
                SecondaryKeywords = new[] { "personal data", "privacy", "data protection", "consent",
                    "data subject", "processing", "controller", "processor", "data breach" },
                Category = "Information Security"
            },

            // 18. ESG
            ["ESG Policy"] = new()
            {
                PrimaryKeywords = new[] { "esg policy", "environmental social governance", "esg" },
                SecondaryKeywords = new[] { "environmental", "social", "governance", "sustainability",
                    "climate", "carbon", "emissions", "green", "responsible business" },
                Category = "ESG & Sustainability"
            },

            // 19. Product Cybersecurity
            ["Product Cybersecurity Policy"] = new()
            {
                PrimaryKeywords = new[] { "product cybersecurity", "product security", "secure product development" },
                SecondaryKeywords = new[] { "product", "cybersecurity", "security by design", "secure development",
                    "vulnerability management", "security testing", "product lifecycle" },
                Category = "Information Security"
            },

            // Additional common types
            ["HR Policy"] = new()
            {
                PrimaryKeywords = new[] { "hr policy", "human resources", "human resource policy" },
                SecondaryKeywords = new[] { "employee", "leave", "attendance", "performance", "recruitment",
                    "compensation", "benefits", "disciplinary" },
                Category = "HR & Workplace"
            },

            ["Finance Policy"] = new()
            {
                PrimaryKeywords = new[] { "finance policy", "financial policy", "accounting policy" },
                SecondaryKeywords = new[] { "finance", "accounting", "budget", "expense", "revenue",
                    "financial control", "authorization" },
                Category = "Finance & Operations"
            },

            ["Quality Policy"] = new()
            {
                PrimaryKeywords = new[] { "quality policy", "quality management", "qms" },
                SecondaryKeywords = new[] { "quality", "standard", "iso", "continuous improvement",
                    "quality control", "quality assurance" },
                Category = "Operations"
            },

            ["Safety Policy"] = new()
            {
                PrimaryKeywords = new[] { "safety policy", "health and safety", "occupational safety", "ehs" },
                SecondaryKeywords = new[] { "safety", "health", "occupational", "accident", "incident",
                    "hazard", "risk", "emergency", "ppe" },
                Category = "HR & Workplace"
            },

            ["Procurement Policy"] = new()
            {
                PrimaryKeywords = new[] { "procurement policy", "purchasing policy", "vendor management" },
                SecondaryKeywords = new[] { "procurement", "purchase", "vendor", "supplier", "contract",
                    "sourcing", "tender", "quotation" },
                Category = "Finance & Operations"
            }
        };

        public string DetectDocumentType(string sectionId, string title, string content)
        {
            var combinedText = $"{sectionId} {title} {content}".ToLower();

            // Score each policy type
            var scores = new Dictionary<string, int>();

            foreach (var (policyName, pattern) in _policyPatterns)
            {
                int score = 0;

                // Primary keywords (weight: 10)
                foreach (var keyword in pattern.PrimaryKeywords)
                {
                    if (combinedText.Contains(keyword.ToLower()))
                    {
                        score += 10;
                        // Bonus if in title or section ID
                        if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                            sectionId.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 5;
                        }
                    }
                }

                // Secondary keywords (weight: 1)
                foreach (var keyword in pattern.SecondaryKeywords)
                {
                    if (combinedText.Contains(keyword.ToLower()))
                    {
                        score += 1;
                    }
                }

                // Exclude keywords (negative score)
                if (pattern.ExcludeKeywords != null)
                {
                    foreach (var keyword in pattern.ExcludeKeywords)
                    {
                        if (combinedText.Contains(keyword.ToLower()))
                        {
                            score -= 5;
                        }
                    }
                }

                if (score > 0)
                {
                    scores[policyName] = score;
                }
            }

            // Return highest scoring policy type
            if (scores.Any())
            {
                var bestMatch = scores.OrderByDescending(kv => kv.Value).First();

                // Only return if score is significant (> 3)
                if (bestMatch.Value > 3)
                {
                    return bestMatch.Key;
                }
            }

            // Fallback
            return "Policy Document";
        }

        // Get category for a document type
        public string GetPolicyCategory(string documentType)
        {
            if (_policyPatterns.TryGetValue(documentType, out var pattern))
            {
                return pattern.Category;
            }
            return "General";
        }

        // Get all keywords for a document type
        public List<string> GetPolicyKeywords(string documentType)
        {
            if (_policyPatterns.TryGetValue(documentType, out var pattern))
            {
                return pattern.PrimaryKeywords
                    .Concat(pattern.SecondaryKeywords)
                    .ToList();
            }
            return new List<string>();
        }

        // ✅ ADD THIS METHOD (around line 250, after DetectDocumentType method)
        public Dictionary<string, DocumentTypePattern> GetAllPolicyPatterns()
        {
            return _policyPatterns;
        }
    }

    // Supporting class
    public class DocumentTypePattern
    {
        public string[] PrimaryKeywords { get; set; } = Array.Empty<string>();
        public string[] SecondaryKeywords { get; set; } = Array.Empty<string>();
        public string[]? ExcludeKeywords { get; set; }
        public string Category { get; set; } = "General";
    }
}