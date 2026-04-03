using System.Collections.Concurrent;
using System.Threading;

internal interface IExpenseStore
{
    IReadOnlyCollection<Expense> GetAll();
    bool TryGet(int id, out Expense expense);
    Expense Add(string description, decimal amount, DateOnly expenseDate, string category);
    bool TryUpdate(int id, string description, decimal amount, DateOnly expenseDate, string category);
    bool TryDelete(int id);
}

internal sealed class ConcurrentExpenseStore : IExpenseStore
{
    private readonly ConcurrentDictionary<int, Expense> expenses = new();
    private int nextExpenseId;

    public ConcurrentExpenseStore(Expense seedExpense)
    {
        expenses[seedExpense.Id] = seedExpense;
        nextExpenseId = seedExpense.Id;
    }

    public IReadOnlyCollection<Expense> GetAll() => expenses.Values.OrderBy(x => x.Id).ToArray();

    public bool TryGet(int id, out Expense expense) => expenses.TryGetValue(id, out expense!);

    public Expense Add(string description, decimal amount, DateOnly expenseDate, string category)
    {
        var id = Interlocked.Increment(ref nextExpenseId);
        var expense = new Expense(id, description, amount, expenseDate, category);
        expenses[id] = expense;
        return expense;
    }

    public bool TryUpdate(int id, string description, decimal amount, DateOnly expenseDate, string category)
    {
        while (true)
        {
            if (!expenses.TryGetValue(id, out var current))
            {
                return false;
            }

            var updated = new Expense(id, description, amount, expenseDate, category);
            if (expenses.TryUpdate(id, updated, current))
            {
                return true;
            }
        }
    }

    public bool TryDelete(int id) => expenses.TryRemove(id, out _);
}
