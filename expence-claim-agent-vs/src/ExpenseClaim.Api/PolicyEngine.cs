internal interface IPolicyEngine
{
    bool HasPolicyText { get; }
    string PolicyText { get; }
    bool TryGetViolation(string description, string category, decimal amount, out PolicyViolation violation);
}

internal sealed class PolicyEngine : IPolicyEngine
{
    private readonly IReadOnlyDictionary<string, PolicyRule> rules;

    public PolicyEngine(string policyText)
    {
        PolicyText = policyText;
        rules = ParsePolicyRules(policyText);
    }

    public bool HasPolicyText => !string.IsNullOrWhiteSpace(PolicyText);

    public string PolicyText { get; }

    public bool TryGetViolation(string description, string category, decimal amount, out PolicyViolation violation)
    {
        violation = default;
        var draft = new ExpenseDraft(description, amount, string.Empty, category);

        if (!TryGetPolicyLimit(draft, out var limit, out var policyCategory) || amount <= limit)
        {
            return false;
        }

        var policyLine = rules.TryGetValue(policyCategory, out var rule)
            ? rule.PolicyLine
            : "Policy limit configured.";

        violation = new PolicyViolation(policyCategory, limit, policyLine);
        return true;
    }

    private bool TryGetPolicyLimit(ExpenseDraft draft, out decimal limit, out string policyCategory)
    {
        limit = 0m;
        policyCategory = string.Empty;

        var normalizedCategory = draft.Category.Trim().ToLowerInvariant();
        var normalizedDescription = draft.Description.Trim().ToLowerInvariant();

        if (IsMatch(normalizedCategory, normalizedDescription, "meals", ["meal", "meals", "breakfast", "lunch", "dinner"]) && TryGetCategoryLimit("Meals", out limit))
        {
            policyCategory = "Meals";
            return true;
        }

        if (IsMatch(normalizedCategory, normalizedDescription, "flights", ["flight", "flights", "airfare"]) && TryGetCategoryLimit("Flights", out limit))
        {
            policyCategory = "Flights";
            return true;
        }

        if (IsMatch(normalizedCategory, normalizedDescription, "taxis and rideshares", ["taxi", "rideshare", "uber", "lyft", "cab"]) && TryGetCategoryLimit("Taxis and Rideshares", out limit))
        {
            policyCategory = "Taxis and Rideshares";
            return true;
        }

        if (IsMatch(normalizedCategory, normalizedDescription, "hotels", ["hotel", "lodging", "accommodation"]) && TryGetCategoryLimit("Hotels", out limit))
        {
            policyCategory = "Hotels";
            return true;
        }

        if (IsMatch(normalizedCategory, normalizedDescription, "other expenses", ["other", "misc", "miscellaneous"]) && TryGetCategoryLimit("Other expenses", out limit))
        {
            policyCategory = "Other expenses";
            return true;
        }

        return false;
    }

    private static bool IsMatch(string category, string description, string categoryKeyword, string[] aliases)
    {
        return category.Contains(categoryKeyword)
            || aliases.Any(a => category.Contains(a) || description.Contains(a));
    }

    private bool TryGetCategoryLimit(string categoryName, out decimal amount)
    {
        if (!rules.TryGetValue(categoryName, out var rule))
        {
            amount = 0m;
            return false;
        }

        amount = rule.Limit;
        return true;
    }

    private static IReadOnlyDictionary<string, PolicyRule> ParsePolicyRules(string policyText)
    {
        var result = new Dictionary<string, PolicyRule>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(policyText))
        {
            return result;
        }

        var lines = policyText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToArray();

        var categories = new[]
        {
            "Meals",
            "Flights",
            "Taxis and Rideshares",
            "Hotels",
            "Other expenses"
        };

        foreach (var category in categories)
        {
            var line = lines.FirstOrDefault(l => l.Contains(category, StringComparison.OrdinalIgnoreCase) && l.Contains('$'));
            if (line is null)
            {
                continue;
            }

            if (TryExtractPolicyAmountFromLine(line, out var limit))
            {
                result[category] = new PolicyRule(limit, line);
            }
        }

        return result;
    }

    private static bool TryExtractPolicyAmountFromLine(string line, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var dollarIndex = line.IndexOf('$');
        if (dollarIndex < 0)
        {
            return false;
        }

        var valueChars = new string(line[(dollarIndex + 1)..].TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return decimal.TryParse(valueChars, out amount);
    }
}

readonly record struct PolicyRule(decimal Limit, string PolicyLine);
readonly record struct PolicyViolation(string PolicyCategory, decimal Limit, string PolicyLine);
