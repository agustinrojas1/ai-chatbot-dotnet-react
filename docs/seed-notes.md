# Seed notes

What `DatabaseSeeder` (`api/src/Chatbot.Infrastructure/Seeding`) creates, and what is ported from the v1 app versus invented. Re-running is safe: it only adds what is missing and never overwrites rows (edits in the database survive a restart).

## Businesses

| Niche | Business | Persona | Time zone | Currency |
|---|---|---|---|---|
| `RealEstate` | PrimeHaven Realty | Sofia | `America/Argentina/Buenos_Aires` | USD |
| `Clinic` | Smile Dental Clinic | Alex | `America/New_York` | USD |
| `Restaurant` | Luigi's Pizzeria | Nico | `America/Chicago` | USD |

Greetings and quick replies are verbatim from v1. Time zones are plausible picks, not v1 data (the v1 listings are in Buenos Aires; the other two are priced in USD, so US zones).

## Persona prompts

`Seeding/Personas/{sofia,alex,nico}.md`, embedded resources. Persona only: voice, WhatsApp formatting rules, conversation style, few-shot examples. Removed on purpose:

- the catalog (now `Offering` rows);
- every v1 line claiming the bot "registered", "noted" or "will send" something (the few-shots were rewritten so they never claim a side effect);
- the v1 `HONESTY RULES`, `[HANDOFF_TRIGGERED]` handoff rules, and language rules (to be replaced by the shared common-rules block, the `handoff_to_human` tool and per-turn language detection).

Kept as a short "BUSINESS FACTS" section because they are neither catalog nor tool rules and have no column yet: clinic hours and insurance policy (Alex), gluten-free crust surcharge, delivery radius and minimum order (Nico). Few-shots still quote a few v1 prices (Nico: Veggie $14, Pepperoni + gluten-free crust = $17).

## Offerings

Ported = name and price from v1. Everything else is invented.

### Real estate (9; attributes: `type`, `neighborhood`, `rooms`, `listing`, `features[]`)

| Offering | Price | Origin |
|---|---|---|
| 2-room apartment in Palermo | 550/month | ported |
| 4-room house in Belgrano | 320,000 sale | ported |
| 3-room townhouse (PH) in Almagro | 600/month | ported |
| 1-room studio in Recoleta | 420/month | invented |
| 3-room apartment in Caballito | 780/month | invented |
| 2-room loft in San Telmo | 650/month | invented |
| 2-room apartment in Villa Crespo | 145,000 sale | invented |
| 3-room apartment in Belgrano | 210,000 sale | invented |
| 5-room house in Núñez | 480,000 sale | invented |

### Clinic (9; attributes: `category`, `durationMinutes`)

| Offering | Price | Origin |
|---|---|---|
| Teeth Cleaning | 80 | ported (description, category, 30 min invented) |
| Teeth Whitening | 200 | ported (description, category, 60 min invented) |
| Check-up & Consultation | 50 | ported (description, category, 30 min invented) |
| Fluoride Treatment | 35 | invented |
| Dental X-Rays | 60 | invented |
| Children's Check-up | 40 | invented |
| Composite Filling | 150 | invented |
| Simple Tooth Extraction | 180 | invented |
| Emergency Dental Visit | 90 | invented |

No orthodontics on purpose: v1 uses it as the "not in the catalog, offer a human" case.

### Restaurant (10; attributes: `category`, `vegetarian`, `glutenFree`, `glutenFreeCrustAvailable`, `glutenFreeCrustSurcharge`, `ingredients[]`)

| Offering | Price | Origin |
|---|---|---|
| Margherita | 12 | ported (description from v1; `vegetarian` derived) |
| Pepperoni | 15 | ported (description from v1) |
| Veggie | 14 | ported (description and ingredients invented; vegetarian per v1 few-shot) |
| Quattro Formaggi | 16 | invented |
| Prosciutto & Mushroom | 17 | invented |
| Caprese Salad | 9 | invented (side, gluten-free, vegetarian) |
| Gluten-Free Penne Arrabbiata | 13 | invented (gluten-free, vegetarian) |
| Garlic Bread | 5 | invented (side) |
| Tiramisu | 7 | invented (dessert) |
| Limonata | 4 | invented (drink, gluten-free) |

`glutenFree` means the item itself is gluten-free. Pizzas are not, but every pizza has `glutenFreeCrustAvailable: true` with a +2 surcharge (v1 policy). The prompt's "+$2" is the same fact.

## Availability slots

Rolling window of 14 days starting at the current UTC date, topped up on every run. Slots that already started are skipped. Existing slots (same business, no offering, same `StartsAt`) are never duplicated, and `Booked` is never touched. Opening hours are in each business's time zone and converted to UTC. All slots are general (`OfferingId = null`): one grid per business, not per property or treatment.

| Business | Hours (local) | Slot | Capacity |
|---|---|---|---|
| PrimeHaven Realty | Mon-Sat 10:00-18:00 (invented) | 30-min visits | 1 |
| Smile Dental Clinic | Mon-Fri 9:00-18:00, Sat 9:00-13:00 (v1) | 30-min appointments | 1 |
| Luigi's Pizzeria | Daily 12:00-22:00 (invented) | 30-min delivery windows | 4 orders |

No unique index was added: the "no duplicates" rule is checked in code, because `OfferingId` is null for these slots and a plain unique index treats nulls as distinct. Two seeders running at the same time (not a supported scenario) could still race.

## ModelPricing

Only one row, verified on <https://ai.google.dev/gemini-api/docs/pricing> on 2026-10-09 (paid tier, standard, per 1M tokens): `gemini-3.5-flash-lite`, input 0.30 USD, output 2.50 USD (the v1 default model). The page also shows 3.5 Flash (1.50 / 9.00) and 3.6 to 3.8 Flash (0.75 / 3.75 until 2026-12-31, then 1.50 / 7.50), not seeded because the model in use is configuration.

No Groq Llama row: <https://console.groq.com/docs/models> lists `llama-3.3-70b-versatile` and `llama-3.1-8b-instant` as "Contact Sales", so there is no public price to seed. Insert it by hand when known. Free-tier calls cost 0 in reality; the row holds the paid tariff so the panel can show what the run would cost.
