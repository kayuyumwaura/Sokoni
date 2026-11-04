# Sokoni — AI sales assistant for Kenyan SMEs on WhatsApp

Built for the Meta Global AI Developer Hackathon (Oct–Nov 2026), indie track, theme "Build for Your World".

Customers chat to a shop on WhatsApp the way they talk, in English, Kiswahili or Sheng. Muse Spark (Meta Model API) answers from the real catalogue, builds the cart, takes the delivery location, sends an M-Pesa STK push, and alerts the owner with a paid order and a map pin.

## Meta models used

- **Muse Spark** (`muse-spark-1.1`): the sales agent, with six tools in a multi-step tool-calling loop.
- **Muse Spark vision:** customers send a photo or screenshot ("do you have this one?"), and the agent matches it to the catalogue.
- **Muse Voice Transcribe** (`muse-voice-transcribe-1.0`): WhatsApp voice notes are decoded from OGG/Opus to 24 kHz WAV in pure C#, then transcribed. Swahili isn't in the model's supported list, so the agent treats voice transcripts as possibly misheard and reads the order back for confirmation before touching the cart.

## Built during the hackathon (Oct 26 – Nov 4, 2026)

Everything in this repository was written during the event:
- Oct 29: core backend (WhatsApp webhook, Muse Spark agent and tools, cart, M-Pesa STK push and callback, owner alerts, admin API, dev simulator)
- Nov 4: voice notes (Muse Voice Transcribe), photo understanding (Muse Spark vision), confirm-back flow for transcripts

## How it works

```
Customer (WhatsApp) ─► Webhook ─► Queue ─► MessageProcessor ─► SalesAgent (Muse Spark + tools)
                                                                    │
                     search_products · update_cart · view_cart · set_delivery
                     request_payment (M-Pesa STK) · handoff_to_owner
                                                                    │
Safaricom ─► /api/mpesa/callback ─► OrderNotifier ─► customer ✅ + owner 🛍️ (WhatsApp)
```

- The model decides what to do; the code enforces what's allowed (real prices, stock, payment only after a delivery location).
- Channel-agnostic core: Instagram DMs plug in by implementing `IMessagingChannel`.
- Owner takeover: `handoff_to_owner`, or any dashboard reply, pauses the assistant for that chat.
- Stack: ASP.NET Core 8 Web API, SQL Server via raw ADO.NET, Meta Model API (OpenAI-compatible Chat Completions), WhatsApp Cloud API, Safaricom Daraja.

## Run it

1. Copy `appsettings.json` to `appsettings.Development.json` (git-ignored) and fill in the keys below.
2. `dotnet run --project Sokoni.Api`. The schema and a demo catalogue are created on startup.

### Settings you need

| Section | Where to get it |
|---|---|
| `ConnectionStrings:Sokoni` | Your SQL Server (local, or the database from your host) |
| `MetaModelApi:ApiKey` | Meta Model API dashboard (your hackathon credits) |
| `WhatsApp:AccessToken`, `PhoneNumberId` | developers.facebook.com → your app → WhatsApp → API Setup |
| `WhatsApp:AppSecret` | App settings → Basic → App secret |
| `WhatsApp:VerifyToken` | Any string you invent; paste the same into the webhook config |
| `Mpesa:*` | developer.safaricom.co.ke → create app (sandbox shortcode 174379 + its passkey) |
| `Mpesa:CallbackBaseUrl` | Your deployed https URL |
| `Mpesa:CallbackKey`, `Security:AdminKey`, `Security:DevKey` | Long random strings you generate |
| `Business:OwnerPhone` | Owner's WhatsApp number, 2547XXXXXXXX |

Leave the M-Pesa keys empty and the flow still runs in simulated-payment mode.

### Connect WhatsApp

1. In your Meta app, WhatsApp → Configuration → Webhook: callback URL `https://<your-host>/api/webhooks/whatsapp`, verify token = `WhatsApp:VerifyToken`.
2. Subscribe to the `messages` field.
3. Under API Setup, add the phones you'll test from (the test number can message up to 5 recipients).
4. The test access token expires after 24 hours. For the demo week, create a System User token in Business Settings.

**24-hour rule:** WhatsApp only allows free-form messages within 24 hours of that person's last message. Have the owner send the bot number a message each morning so order alerts get through.

## Test without WhatsApp (dev mode)

Set `Security:DevEnabled=true` and a `DevKey`, then:

```bash
curl -X POST localhost:5000/api/dev/chat -H "X-Dev-Key: <key>" -H "Content-Type: application/json" \
  -d '{"customerId":"test1","name":"Wanjiru","text":"sasa, mko na lipstick ya red? bei gani"}'

curl -X POST localhost:5000/api/dev/chat -H "X-Dev-Key: <key>" -H "Content-Type: application/json" \
  -d '{"customerId":"test1","latitude":-1.3966,"longitude":36.7442,"locationLabel":"Rongai"}'

# Voice note or photo (send one to yourself on WhatsApp, download it, upload it here)
curl -X POST localhost:5000/api/dev/media -H "X-Dev-Key: <key>" -F customerId=test1 -F file=@voice-note.ogg

curl -X POST localhost:5000/api/dev/simulate-payment/1 -H "X-Dev-Key: <key>" -H "Content-Type: application/json" -d '{}'
```

## Owner dashboard

Open `https://<your-host>/admin` and enter `Security:AdminKey`. It works on a phone and refreshes every 8 seconds.

- **Orders:** today's sales, orders to dispatch, awaiting payment, chats needing the owner; filter by status; mark *on the way* / *delivered* / *cancel* (the customer is notified on WhatsApp); map link for the delivery pin.
- **Chats:** every conversation, voice notes shown with their transcript, photos with their description; reply as the owner, take over a chat, or hand it back to the assistant.
- **Products:** edit names, prices, stock and availability. The assistant uses changes immediately.

## Admin API (dashboard backend)

All need header `X-Admin-Key`.

- `GET /api/admin/orders?status=Paid`
- `POST /api/admin/orders/{id}/status` `{"status":"Dispatched"}` notifies the customer
- `GET /api/admin/conversations`, `GET /api/admin/conversations/{id}/messages`
- `POST /api/admin/conversations/{id}/reply` `{"text":"..."}` sends as the owner and pauses the assistant
- `POST /api/admin/conversations/{id}/takeover` `{"on":false}` hands back to the assistant
- `GET/POST /api/admin/products`

## Deploy

Any host with .NET 8 and SQL Server works (e.g. MonsterASP.NET). `dotnet publish Sokoni.Api -c Release -o publish`, upload, then set the settings as environment variables (`MetaModelApi__ApiKey`, `WhatsApp__AccessToken`, …) rather than committing them.
