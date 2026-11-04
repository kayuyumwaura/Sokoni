namespace Sokoni;

/// <summary>Meta Model API (Muse Spark). OpenAI-compatible Chat Completions.</summary>
public sealed class MuseOptions
{
    public string BaseUrl { get; set; } = "https://api.meta.ai/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "muse-spark-1.1";
    public int MaxToolRounds { get; set; } = 6;
    public int HistoryMessages { get; set; } = 30;

    /// <summary>Muse Voice Transcribe model for voice notes. Empty turns voice notes off.</summary>
    public string TranscribeModel { get; set; } = "muse-voice-transcribe-1.0";
    /// <summary>Language hints for transcription. Swahili isn't in the supported list, so English is the closest hint for Kenyan code-switching.</summary>
    public string[] TranscribeLanguageBias { get; set; } = { "English" };
    /// <summary>Lets Muse Spark look at customer photos ("do you have this one?").</summary>
    public bool VisionEnabled { get; set; } = true;
}

/// <summary>WhatsApp Cloud API.</summary>
public sealed class WhatsAppOptions
{
    public string GraphVersion { get; set; } = "v23.0";
    public string AccessToken { get; set; } = "";
    public string PhoneNumberId { get; set; } = "";
    /// <summary>Any string you choose; paste the same value into the Meta webhook config.</summary>
    public string VerifyToken { get; set; } = "";
    /// <summary>Meta app secret, used to check X-Hub-Signature-256 on incoming webhooks.</summary>
    public string AppSecret { get; set; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccessToken) && !string.IsNullOrWhiteSpace(PhoneNumberId);
}

/// <summary>Safaricom Daraja (M-Pesa Express / STK push).</summary>
public sealed class MpesaOptions
{
    /// <summary>"sandbox" or "production".</summary>
    public string Environment { get; set; } = "sandbox";
    public string ConsumerKey { get; set; } = "";
    public string ConsumerSecret { get; set; } = "";
    public string ShortCode { get; set; } = "174379";
    public string Passkey { get; set; } = "";
    /// <summary>"CustomerPayBillOnline" (paybill) or "CustomerBuyGoodsOnline" (till).</summary>
    public string TransactionType { get; set; } = "CustomerPayBillOnline";
    /// <summary>Public https base URL of this app, e.g. https://sokoni.example.com</summary>
    public string CallbackBaseUrl { get; set; } = "";
    /// <summary>Random secret placed in the callback path so strangers can't fake payments.</summary>
    public string CallbackKey { get; set; } = "";
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ConsumerKey) && !string.IsNullOrWhiteSpace(ConsumerSecret) &&
        !string.IsNullOrWhiteSpace(Passkey) && !string.IsNullOrWhiteSpace(CallbackBaseUrl) &&
        !string.IsNullOrWhiteSpace(CallbackKey);
}

/// <summary>The business the assistant sells for.</summary>
public sealed class BusinessOptions
{
    public string Name { get; set; } = "Demo Beauty Shop";
    public string Description { get; set; } = "A Nairobi beauty and cosmetics shop.";
    public string Hours { get; set; } = "Mon–Sat, 8am–7pm";
    public decimal DeliveryFee { get; set; } = 250;
    public string DeliveryInfo { get; set; } = "Same-day delivery within Nairobi for orders paid before 3pm; next day otherwise.";
    /// <summary>Owner's WhatsApp number in 2547XXXXXXXX format, for order and handoff alerts.</summary>
    public string OwnerPhone { get; set; } = "";
}

public sealed class SecurityOptions
{
    /// <summary>Required in the X-Admin-Key header for /api/admin/*.</summary>
    public string AdminKey { get; set; } = "";
    /// <summary>Turns on /api/dev/* (chat simulator, fake payments). Keep false in production.</summary>
    public bool DevEnabled { get; set; }
    /// <summary>Required in the X-Dev-Key header for /api/dev/*.</summary>
    public string DevKey { get; set; } = "";
}
