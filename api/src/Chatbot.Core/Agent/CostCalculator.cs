using Chatbot.Core.Domain;

namespace Chatbot.Core.Agent;

public static class CostCalculator
{
    /// <summary>Cost in USD: tokens × price per million. Unknown model (no pricing row) costs 0.</summary>
    public static decimal Compute(ModelPricing? pricing, long tokensIn, long tokensOut)
    {
        if (pricing is null)
        {
            return 0m;
        }

        return (tokensIn * pricing.InputPerMillionUsd + tokensOut * pricing.OutputPerMillionUsd) / 1_000_000m;
    }
}
