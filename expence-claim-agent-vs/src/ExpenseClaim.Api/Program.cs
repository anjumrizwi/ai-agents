using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;

const string ExpensePolicyFileName = "expense-policy.txt";
const string FoundryEndpointEnvVar = "AZURE_FOUNDRY_PROJECT_ENDPOINT";
const string FoundryDeploymentEnvVar = "AZURE_FOUNDRY_PROJECT_DEPLOYMENT_NAME";
const string FoundryEndpointConfigKey = "AzureAiFoundry:ProjectEndpoint";
const string FoundryDeploymentConfigKey = "AzureAiFoundry:DeploymentName";

var expenseDraftJsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<Func<AIProjectClient?>>(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();

    return () =>
    {
        var endpoint = Environment.GetEnvironmentVariable(FoundryEndpointEnvVar)
            ?? configuration[FoundryEndpointConfigKey];

        return string.IsNullOrWhiteSpace(endpoint)
            ? null
            : new AIProjectClient(new Uri(endpoint), new DefaultAzureCredential());
    };
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

var seedExpense = new Expense(1, "Taxi", 35.50m, DateOnly.FromDateTime(DateTime.UtcNow.Date), "Travel");
var expenses = new ConcurrentDictionary<int, Expense>();
expenses.TryAdd(seedExpense.Id, seedExpense);
var nextExpenseId = seedExpense.Id;

var policyFilePath = Path.Combine(app.Environment.ContentRootPath, ExpensePolicyFileName);
var expensePolicyText = File.Exists(policyFilePath)
    ? File.ReadAllText(policyFilePath)
    : string.Empty;
var policyRules = ParsePolicyRules(expensePolicyText);

app.MapGet("/", () => Results.Ok(new { message = "ExpenseClaim API is running", endpoints = new[] { "/api/expenses" } }));

var expenseRoutes = app.MapGroup("/api/expenses");

expenseRoutes.MapGet("/", () => Results.Ok(expenses.Values.OrderBy(x => x.Id)));

expenseRoutes.MapGet("/{id:int}", (int id) =>
{
    return expenses.TryGetValue(id, out var expense)
        ? Results.Ok(expense)
        : Results.NotFound();
});

expenseRoutes.MapPost("/", (CreateExpenseRequest request) =>
{
    if (TryGetPolicyViolation(policyRules, request.Description, request.Category, request.Amount, out var postViolation))
    {
        return Results.BadRequest(new
        {
            error = $"{postViolation.PolicyCategory} expense exceeds policy limit.",
            policy = postViolation.PolicyLine,
            limit = postViolation.Limit
        });
    }

    var nextId = Interlocked.Increment(ref nextExpenseId);
    var expense = new Expense(nextId, request.Description, request.Amount, request.ExpenseDate, request.Category);
    expenses[nextId] = expense;
    return Results.Created($"/api/expenses/{expense.Id}", expense);
});

expenseRoutes.MapPost("/ai", async (CreateExpenseFromTextRequest request, Func<AIProjectClient?> projectClientFactory) =>
{
    var projectClient = projectClientFactory();
    var deploymentName = Environment.GetEnvironmentVariable(FoundryDeploymentEnvVar)
        ?? builder.Configuration[FoundryDeploymentConfigKey];

    if (projectClient is null || string.IsNullOrWhiteSpace(deploymentName))
    {
        return Results.BadRequest(new
        {
            error = "Missing Azure Foundry configuration.",
            required = new[]
            {
                "AzureAiFoundry:ProjectEndpoint (or AZURE_FOUNDRY_PROJECT_ENDPOINT)",
                "AzureAiFoundry:DeploymentName (or AZURE_FOUNDRY_PROJECT_DEPLOYMENT_NAME)"
            }
        });
    }

    AIAgent? agent = null;

    try
    {
        if (LooksLikePolicyQuestion(request.Text))
        {
            if (string.IsNullOrWhiteSpace(expensePolicyText))
            {
                return Results.BadRequest(new
                {
                    error = "Expense policy knowledge file is missing.",
                    required = ExpensePolicyFileName
                });
            }

            #pragma warning disable CS0618
            agent = await projectClient.CreateAIAgentAsync(
                name: "ExpensePolicyAssistant",
                model: deploymentName,
                instructions: "You are an expense policy assistant. Answer only using the policy text below. If answer is not in policy, say that clearly.\n\nExpense Policy:\n" + expensePolicyText);
            #pragma warning restore CS0618

            var policyAnswer = (await agent.RunAsync(request.Text)).ToString()?.Trim();

            if (string.IsNullOrWhiteSpace(policyAnswer))
            {
                return Results.BadRequest(new { error = "AI response was empty." });
            }

            return Results.Ok(new { answer = policyAnswer });
        }

        #pragma warning disable CS0618
        agent = await projectClient.CreateAIAgentAsync(
            name: "ExpenseClaimExtractor",
            model: deploymentName,
            instructions: "Extract a single expense claim from user text. Return strict JSON only with: description (string), amount (number), expenseDate (yyyy-MM-dd), category (string). No markdown and no extra keys.");
        #pragma warning restore CS0618

        var responseText = (await agent.RunAsync(request.Text)).ToString()?.Trim();

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return Results.BadRequest(new { error = "AI response was empty." });
        }

        var json = responseText;

        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            json = json.Trim('`').Trim();
            if (json.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            {
                json = json[4..].Trim();
            }
        }

        var objectStart = json.IndexOf('{');
        var objectEnd = json.LastIndexOf('}');
        if (objectStart >= 0 && objectEnd > objectStart)
        {
            json = json[objectStart..(objectEnd + 1)];
        }

        ExpenseDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<ExpenseDraft>(json, expenseDraftJsonOptions);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Could not parse expense details from AI response." });
        }

        if (draft is null || string.IsNullOrWhiteSpace(draft.Description) || string.IsNullOrWhiteSpace(draft.Category))
        {
            return Results.BadRequest(new
            {
                error = "Could not parse expense details from AI response.",
                hint = "Provide a claim statement with amount, date, description, and category."
            });
        }

        if (!DateOnly.TryParse(draft.ExpenseDate, out var expenseDate))
        {
            expenseDate = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        }

        if (TryGetPolicyViolation(policyRules, draft.Description, draft.Category, draft.Amount, out var aiViolation))
        {
            return Results.BadRequest(new
            {
                error = $"{aiViolation.PolicyCategory} expense exceeds policy limit.",
                policy = aiViolation.PolicyLine,
                limit = aiViolation.Limit
            });
        }

        var nextId = Interlocked.Increment(ref nextExpenseId);
        var expense = new Expense(nextId, draft.Description, draft.Amount, expenseDate, draft.Category);
        expenses[nextId] = expense;

        return Results.Created($"/api/expenses/{expense.Id}", expense);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        return Results.Problem("AI expense extraction failed.");
    }
    finally
    {
        if (agent is not null && projectClient is not null)
        {
            try
            {
                await projectClient.Agents.DeleteAgentAsync(agent.Name);
            }
            catch (Exception cleanupEx)
            {
                Console.Error.WriteLine(cleanupEx);
            }
        }
    }
});

expenseRoutes.MapPut("/{id:int}", (int id, UpdateExpenseRequest request) =>
{
    if (TryGetPolicyViolation(policyRules, request.Description, request.Category, request.Amount, out var putViolation))
    {
        return Results.BadRequest(new
        {
            error = $"{putViolation.PolicyCategory} expense exceeds policy limit.",
            policy = putViolation.PolicyLine,
            limit = putViolation.Limit
        });
    }

    while (true)
    {
        if (!expenses.TryGetValue(id, out var existingExpense))
        {
            return Results.NotFound();
        }

        var updatedExpense = new Expense(id, request.Description, request.Amount, request.ExpenseDate, request.Category);
        if (expenses.TryUpdate(id, updatedExpense, existingExpense))
        {
            return Results.NoContent();
        }
    }
});

expenseRoutes.MapDelete("/{id:int}", (int id) =>
{
    return expenses.TryRemove(id, out _)
        ? Results.NoContent()
        : Results.NotFound();
});

app.Run();

static bool LooksLikePolicyQuestion(string text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return true;
    }

    var value = text.Trim();
    if (value.EndsWith("?", StringComparison.Ordinal))
    {
        return true;
    }

    var lower = value.ToLowerInvariant();
    return lower.StartsWith("what")
        || lower.StartsWith("how much")
        || lower.StartsWith("can i")
        || lower.StartsWith("is ")
        || lower.StartsWith("are ")
        || lower.Contains("maximum i can claim")
        || lower.Contains("max claim")
        || lower.Contains("policy");
}

static bool TryGetPolicyLimit(IReadOnlyDictionary<string, PolicyRule> policyRules, ExpenseDraft draft, out decimal limit, out string policyCategory)
{
    limit = 0m;
    policyCategory = string.Empty;

    var normalizedCategory = draft.Category.Trim().ToLowerInvariant();
    var normalizedDescription = draft.Description.Trim().ToLowerInvariant();

    if (IsMatch(normalizedCategory, normalizedDescription, "meals", ["meal", "meals", "breakfast", "lunch", "dinner"]) && TryGetPolicyCategoryLimit(policyRules, "Meals", out limit))
    {
        policyCategory = "Meals";
        return true;
    }

    if (IsMatch(normalizedCategory, normalizedDescription, "flights", ["flight", "flights", "airfare"]) && TryGetPolicyCategoryLimit(policyRules, "Flights", out limit))
    {
        policyCategory = "Flights";
        return true;
    }

    if (IsMatch(normalizedCategory, normalizedDescription, "taxis and rideshares", ["taxi", "rideshare", "uber", "lyft", "cab"]) && TryGetPolicyCategoryLimit(policyRules, "Taxis and Rideshares", out limit))
    {
        policyCategory = "Taxis and Rideshares";
        return true;
    }

    if (IsMatch(normalizedCategory, normalizedDescription, "hotels", ["hotel", "lodging", "accommodation"]) && TryGetPolicyCategoryLimit(policyRules, "Hotels", out limit))
    {
        policyCategory = "Hotels";
        return true;
    }

    if (IsMatch(normalizedCategory, normalizedDescription, "other expenses", ["other", "misc", "miscellaneous"]) && TryGetPolicyCategoryLimit(policyRules, "Other expenses", out limit))
    {
        policyCategory = "Other expenses";
        return true;
    }

    return false;
}

static bool IsMatch(string category, string description, string categoryKeyword, string[] aliases)
{
    return category.Contains(categoryKeyword)
        || aliases.Any(a => category.Contains(a) || description.Contains(a));
}

static bool TryGetPolicyCategoryLimit(IReadOnlyDictionary<string, PolicyRule> policyRules, string categoryName, out decimal amount)
{
    if (!policyRules.TryGetValue(categoryName, out var rule))
    {
        amount = 0m;
        return false;
    }

    amount = rule.Limit;
    return true;
}

static IReadOnlyDictionary<string, PolicyRule> ParsePolicyRules(string policyText)
{
    var rules = new Dictionary<string, PolicyRule>(StringComparer.OrdinalIgnoreCase);
    if (string.IsNullOrWhiteSpace(policyText))
    {
        return rules;
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
            rules[category] = new PolicyRule(limit, line);
        }
    }

    return rules;
}

static bool TryExtractPolicyAmountFromLine(string line, out decimal amount)
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

static bool TryGetPolicyViolation(IReadOnlyDictionary<string, PolicyRule> policyRules, string description, string category, decimal amount, out PolicyViolation violation)
{
    violation = default;
    var draft = new ExpenseDraft(description, amount, string.Empty, category);
    if (!TryGetPolicyLimit(policyRules, draft, out var limit, out var policyCategory) || amount <= limit)
    {
        return false;
    }

    var policyLine = policyRules.TryGetValue(policyCategory, out var policyRule)
        ? policyRule.PolicyLine
        : "Policy limit configured.";

    violation = new PolicyViolation(policyCategory, limit, policyLine);
    return true;
}

readonly record struct PolicyRule(decimal Limit, string PolicyLine);

readonly record struct PolicyViolation(string PolicyCategory, decimal Limit, string PolicyLine);

record Expense(int Id, string Description, decimal Amount, DateOnly ExpenseDate, string Category);

record CreateExpenseRequest(string Description, decimal Amount, DateOnly ExpenseDate, string Category);

record UpdateExpenseRequest(string Description, decimal Amount, DateOnly ExpenseDate, string Category);

record CreateExpenseFromTextRequest(string Text);

sealed record ExpenseDraft(string Description, decimal Amount, string ExpenseDate, string Category);

public partial class Program;
