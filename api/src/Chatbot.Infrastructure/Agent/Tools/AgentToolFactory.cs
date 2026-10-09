using Chatbot.Infrastructure.Persistence;
using Microsoft.Extensions.AI;

namespace Chatbot.Infrastructure.Agent.Tools;

/// <summary>
/// Builds the five tools for one request. The tools capture <paramref name="db"/> and <paramref name="context"/>, so
/// business and session ids come from the server and never from the model. They share one DbContext, which is safe
/// because <c>FunctionInvokingChatClient</c> invokes tool calls one at a time.
/// </summary>
public static class AgentToolFactory
{
    public const string SearchCatalog = "search_catalog";
    public const string GetOfferingDetails = "get_offering_details";
    public const string CheckAvailability = "check_availability";
    public const string CreateBooking = "create_booking";
    public const string HandoffToHuman = "handoff_to_human";

    public static IList<AITool> Create(ChatbotDbContext db, ToolContext context, TimeProvider clock)
    {
        var catalog = new CatalogTools(db, context);
        var availability = new AvailabilityTools(db, context, clock);
        var booking = new BookingTools(db, context, clock);
        var handoff = new HandoffTools(db, context, clock);

        return
        [
            AIFunctionFactory.Create(catalog.SearchCatalogAsync, SearchCatalog,
                "Search the active catalog of this business (properties, treatments or menu items). Returns at most 5 offerings with id, name, price and key attributes. " +
                "Always call it before stating prices or what is available; never answer from memory."),
            AIFunctionFactory.Create(catalog.GetOfferingDetailsAsync, GetOfferingDetails,
                "Get the full details of one offering by offeringId, or 'not found'."),
            AIFunctionFactory.Create(availability.CheckAvailabilityAsync, CheckAvailability,
                "List free slots (visits, appointments, delivery windows) between two dates, at most 14 days apart. Returns at most 6 slots with slotId and the time in the business time zone. " +
                "Always call it before offering a time."),
            AIFunctionFactory.Create(booking.CreateBookingAsync, CreateBooking,
                "Register a booking, appointment, visit or order. Call it only after the customer explicitly confirmed the full summary (what, when, name, phone). " +
                "Provide a slotId, or items for an order (or both). The result is registered and PENDING confirmation by the business, never confirmed. " +
                "Safe to retry: the same request returns the same booking."),
            AIFunctionFactory.Create(handoff.HandoffToHumanAsync, HandoffToHuman,
                "Hand the conversation to a person of the business. Use it when the customer asks for a person, or asks something outside the catalog and accepts to be referred. " +
                "Not for small talk or unintelligible messages."),
        ];
    }
}
