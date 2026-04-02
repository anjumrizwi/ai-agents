using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ExpenseClaim.Tests;

public class ApiTests
{
    [Fact]
    public async Task GetExpenses_ReturnsSeedExpense()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/expenses");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var expenses = await response.Content.ReadFromJsonAsync<List<ExpenseDto>>();
        Assert.NotNull(expenses);
        Assert.NotEmpty(expenses);
        Assert.Contains(expenses, e => e.Description == "Taxi" && e.Category == "Travel");
    }

    [Fact]
    public async Task PostExpense_ThenGetById_ReturnsCreatedExpense()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var payload = new
        {
            description = "Hotel",
            amount = 120.75m,
            expenseDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            category = "Lodging"
        };

        var createResponse = await client.PostAsJsonAsync("/api/expenses", payload);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<ExpenseDto>();
        Assert.NotNull(created);

        var getResponse = await client.GetAsync($"/api/expenses/{created!.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var fetched = await getResponse.Content.ReadFromJsonAsync<ExpenseDto>();
        Assert.NotNull(fetched);
        Assert.Equal("Hotel", fetched!.Description);
        Assert.Equal("Lodging", fetched.Category);
        Assert.Equal(120.75m, fetched.Amount);
    }

    [Fact]
    public async Task PutExpense_ThenDeleteExpense_UpdatesAndRemovesExpense()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var createPayload = new
        {
            description = "Meal",
            amount = 45.00m,
            expenseDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            category = "Food"
        };

        var createResponse = await client.PostAsJsonAsync("/api/expenses", createPayload);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<ExpenseDto>();
        Assert.NotNull(created);

        var updatePayload = new
        {
            description = "Client Meal",
            amount = 55.25m,
            expenseDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            category = "Entertainment"
        };

        var updateResponse = await client.PutAsJsonAsync($"/api/expenses/{created!.Id}", updatePayload);
        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

        var getUpdatedResponse = await client.GetAsync($"/api/expenses/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getUpdatedResponse.StatusCode);

        var updated = await getUpdatedResponse.Content.ReadFromJsonAsync<ExpenseDto>();
        Assert.NotNull(updated);
        Assert.Equal("Client Meal", updated!.Description);
        Assert.Equal("Entertainment", updated.Category);
        Assert.Equal(55.25m, updated.Amount);

        var deleteResponse = await client.DeleteAsync($"/api/expenses/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getDeletedResponse = await client.GetAsync($"/api/expenses/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getDeletedResponse.StatusCode);
    }

    [Fact]
    public async Task PostExpenseAi_WhenConfigurationMissing_ReturnsBadRequest()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var previousEndpoint = Environment.GetEnvironmentVariable("AZURE_FOUNDRY_PROJECT_ENDPOINT");
        var previousDeployment = Environment.GetEnvironmentVariable("AZURE_FOUNDRY_PROJECT_DEPLOYMENT_NAME");

        Environment.SetEnvironmentVariable("AZURE_FOUNDRY_PROJECT_ENDPOINT", null);
        Environment.SetEnvironmentVariable("AZURE_FOUNDRY_PROJECT_DEPLOYMENT_NAME", null);

        try
        {
            var response = await client.PostAsJsonAsync("/api/expenses/ai", new { text = "I spent 20 on coffee" });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
            Assert.NotNull(error);
            Assert.Equal("Missing Azure Foundry configuration.", error!.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_FOUNDRY_PROJECT_ENDPOINT", previousEndpoint);
            Environment.SetEnvironmentVariable("AZURE_FOUNDRY_PROJECT_DEPLOYMENT_NAME", previousDeployment);
        }
    }

    private sealed record ExpenseDto(int Id, string Description, decimal Amount, DateOnly ExpenseDate, string Category);

    private sealed record ErrorDto(string Error);
}
