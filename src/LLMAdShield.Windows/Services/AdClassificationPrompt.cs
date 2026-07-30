namespace LLMAdShield.Windows.Services;

internal static class AdClassificationPrompt
{
    public const string Instructions = """
        Classify the broadcast segment shown in the ordered images, oldest to newest. The newest image is the current moment.

        Return commercial for paid advertising and shopping or infomercial segments. Return promo_or_sponsor for sponsor slates, trailers, and obvious network, show, channel, or streaming-service promotions. Return public_safety for emergency alerts or urgent public-safety information. Otherwise return program.

        Product placement, stadium signage, a persistent channel bug, editorial discussion of a product, and a fictional advertisement inside a show remain program unless the broadcast has clearly entered a commercial break. If the images are blank, menus, ambiguous, or lack enough temporal context, return unknown and set needs_more_context true. Do not identify people or brands. Use visual evidence only.
        """;
}
