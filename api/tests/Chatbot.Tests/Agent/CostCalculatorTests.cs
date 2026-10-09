using Chatbot.Core.Agent;
using Chatbot.Core.Domain;

namespace Chatbot.Tests.Agent;

public class CostCalculatorTests
{
    private static readonly ModelPricing Pricing = new() { Model = "m", InputPerMillionUsd = 0.15m, OutputPerMillionUsd = 0.60m };

    [Fact]
    public void Null_pricing_costs_zero()
    {
        Assert.Equal(0m, CostCalculator.Compute(null, 1_000, 500));
    }

    [Fact]
    public void Computes_tokens_times_price_per_million()
    {
        // 1M in * 0.15 + 2M out * 0.60 = 1.35
        Assert.Equal(1.35m, CostCalculator.Compute(Pricing, 1_000_000, 2_000_000));
    }

    [Fact]
    public void Keeps_decimal_precision_for_small_runs()
    {
        // 1234 * 0.15 / 1M = 0.0001851 ; 567 * 0.60 / 1M = 0.0003402
        Assert.Equal(0.0005253m, CostCalculator.Compute(Pricing, 1_234, 567));
    }

    [Fact]
    public void Zero_tokens_cost_zero()
    {
        Assert.Equal(0m, CostCalculator.Compute(Pricing, 0, 0));
    }

    [Fact]
    public void Free_model_costs_zero()
    {
        var free = new ModelPricing { Model = "free", InputPerMillionUsd = 0m, OutputPerMillionUsd = 0m };

        Assert.Equal(0m, CostCalculator.Compute(free, 5_000, 5_000));
    }

    [Fact]
    public void Large_token_counts_do_not_overflow()
    {
        Assert.Equal(15_000m, CostCalculator.Compute(
            new ModelPricing { Model = "big", InputPerMillionUsd = 5m, OutputPerMillionUsd = 10m }, 1_000_000_000, 1_000_000_000));
    }
}
