using Chatbot.Core.Agent;

namespace Chatbot.Tests.Agent;

public class PhoneMaskerTests
{
    [Theory]
    [InlineData("+54 11 5555-4444", "+54 11 ****-4444")]
    [InlineData("11 5555 4444", "11 **** 4444")]
    [InlineData("+1 (415) 555-2671", "+1 (415) ***-2671")]
    [InlineData("555-1234", "***-1234")]
    [InlineData("+5491155554444", "+54*******4444")]
    [InlineData("1155554444", "11****4444")]
    [InlineData("1234", "****")]
    [InlineData("", "")]
    [InlineData("no phone here", "no phone here")]
    public void Mask_keeps_last_four_digits_and_country_code(string phone, string expected)
    {
        Assert.Equal(expected, PhoneMasker.Mask(phone));
    }

    [Fact]
    public void Mask_never_changes_the_length()
    {
        const string phone = "+54 9 11 5555-4444";

        Assert.Equal(phone.Length, PhoneMasker.Mask(phone).Length);
        Assert.Equal("+54 9 11 ****-4444", PhoneMasker.Mask(phone));
    }

    [Theory]
    [InlineData("Call me at +54 11 5555-4444 please", "Call me at +54 11 ****-4444 please")]
    [InlineData("""{"customerName":"Ana","customerPhone":"+54 11 5555-4444"}""", """{"customerName":"Ana","customerPhone":"+54 11 ****-4444"}""")]
    [InlineData("Mi número es 11 5555 4444.", "Mi número es 11 **** 4444.")]
    [InlineData("tel 1155554444", "tel 11****4444")]
    [InlineData("a +54 11 5555-4444 and b 555-1234", "a +54 11 ****-4444 and b ***-1234")]
    public void MaskInText_masks_phones_inside_text_and_json(string text, string expected)
    {
        Assert.Equal(expected, PhoneMasker.MaskInText(text));
    }

    [Theory]
    [InlineData("No phone in this message")]
    [InlineData("The 2-room apartment costs 1.500.000 ARS")]
    [InlineData("""{"price":1500000.50,"rooms":2}""")]
    [InlineData("Price 12500000")]
    [InlineData("Visit on 2026-10-09 at 10:30")]
    [InlineData("Visit on 09-10-2026")]
    [InlineData("""{"startsAt":"2026-10-09T10:30:00-03:00"}""")]
    [InlineData("Table for 2 at 8 pm, 20 30 minutes")]
    [InlineData("Slot 019a1b2c-3d4e-7f80-9a1b-2c3d4e5f6a7b")]
    public void MaskInText_leaves_text_without_phones_untouched(string text)
    {
        Assert.Equal(text, PhoneMasker.MaskInText(text));
    }
}
