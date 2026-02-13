# DealsScannerPro - Admin Guide

## Hurtig Oversigt

DealsScannerPro scanner automatisk tilbudsaviser (PDF) fra danske supermarkeder og gør tilbuddene tilgængelige via en søgeside.

**Webadresser:**

| Side | URL |
|------|-----|
| Søgeside (forbruger) | https://stdealscannerprod.z6.web.core.windows.net/ |
| Review UI | https://stdealscannerprod.z6.web.core.windows.net/review.html |
| Learning UI | https://stdealscannerprod.z6.web.core.windows.net/learning.html |
| Diagnostik | https://stdealscannerprod.z6.web.core.windows.net/diagnostics.html |
| API | https://func-dealscanner-prod.azurewebsites.net/api/ |

---

## 1. Upload en tilbudsavis (kom i gang)

Der er to måder at uploade en PDF-tilbudsavis:

### Automatisk (anbefalet)

Upload PDF'en til Azure Blob Storage containeren `tilbudsaviser`. Systemet scanner automatisk filen og uploader tilbuddene.

**Filnavn-format:** `{butik}_{år}-uge{ugenummer}.pdf`

Eksempler:
- `netto_2025-uge50.pdf`
- `rema_2025-uge51.pdf`

**Sådan gør du:**
1. Åbn Azure Portal > Storage Account `stdealscannerprod`
2. Gå til **Containers** > **tilbudsaviser**
3. Klik **Upload** og vælg PDF-filen
4. Vent 2-3 minutter mens scanneren behandler filen
5. Filen flyttes automatisk til `processed/` mappen når den er færdig

### Manuel (via API)

Hvis du allerede har scannet data i JSON-format, kan du uploade direkte:

```
POST https://func-dealscanner-prod.azurewebsites.net/api/management/upload/v2
Header: x-api-key: {din API-nøgle}
Body: JSON med tilbudsdata
```

> API-nøglen findes i Azure Key Vault under `kv-dealscanner-prod`.

---

## 2. Søg i tilbud

### Via søgesiden

1. Gå til https://stdealscannerprod.z6.web.core.windows.net/
2. Skriv hvad du leder efter (f.eks. "mælk", "kylling", "Lurpak")
3. Vælg indkøbsdato for at se aktuelle tilbud
4. Filtrer eventuelt på butik

Søgningen tolererer stavefejl (fuzzy search).

### Via API

| Hvad | URL |
|------|-----|
| Alle tilbud | `/api/deals` |
| Filtrer på butik | `/api/deals?butik=netto` |
| Filtrer på kategori | `/api/deals?kategori=mejeri` |
| Begræns antal | `/api/deals?limit=50` |
| Kombiner filtre | `/api/deals?butik=netto&kategori=koed&limit=20` |
| Enkelt tilbud | `/api/deals/{id}` |
| Søg med tekst | `/api/tilbud/search?q=mælk&dato=2025-12-15` |
| Se butikker | `/api/stores` |
| Se kategorier | `/api/categories` |

---

## 3. Gennemse tilbud (Review)

Tilbud med lav konfidens (under 90%) skal gennemses manuelt.

### Åbn Review UI

1. Gå til https://stdealscannerprod.z6.web.core.windows.net/review.html
2. Log ind med demo-kode: **demo123**
3. Du ser nu en liste af tilbud der venter på gennemsyn

### Gennemse tilbud

For hvert tilbud kan du:
- **Godkend** - tilbuddet er korrekt
- **Afvis** - tilbuddet er forkert (f.eks. ikke et reelt tilbud)
- **Rediger** - ret produktnavn, pris eller mængde

### Batch-handlinger

1. Marker flere tilbud med checkbokse
2. Klik **Godkend alle** eller **Afvis alle** for hurtig behandling

### Filtrering

- Filtrer på butik for at fokusere på én kæde ad gangen
- Sorter efter konfidens for at tage de mest usikre først

---

## 4. Learning Mode (forbedring)

Learning UI hjælper med at forbedre scannerens nøjagtighed ved at vælge mellem kandidat-værdier.

### Åbn Learning UI

1. Gå til https://stdealscannerprod.z6.web.core.windows.net/learning.html
2. Log ind med demo-kode: **demo123**

### Sådan bruges det

For hvert tilbud viser systemet **kandidater** - mulige værdier for pris og mængde:

1. Se de foreslåede **pris-kandidater** (klikbare chips)
2. Klik på den korrekte pris
3. Se de foreslåede **mængde-kandidater**
4. Klik på den korrekte mængde
5. Rettelsen gemmes automatisk på tilbuddet

### Reprocess

Når du har rettet nok tilbud, kan du trigger en reprocessering:
- Systemet anvender dine rettelser som regler for fremtidige scanninger
- Dette forbedrer automatisk kvaliteten over tid

---

## 5. Diagnostik

### Åbn Diagnostik

1. Gå til https://stdealscannerprod.z6.web.core.windows.net/diagnostics.html
2. Se systemets sundhedsstatus

### Hvad kan du se

- **System Health** - Er GPT og Document Intelligence aktive?
- **Scan historik** - Hvilke scanninger er kørt, og hvad var resultatet?
- **Service status** - Hvilke services blev brugt per scanning?
- **Advarsler** - Hvis systemet falder tilbage til alternative metoder

---

## 6. Daglig drift

### Typisk arbejdsgang

1. **Upload ny tilbudsavis** → Blob Storage `tilbudsaviser`
2. **Vent 2-3 min** → Scanneren behandler automatisk
3. **Tjek diagnostik** → Se at scanningen lykkedes
4. **Review tilbud** → Gennemse lavkonfidens-tilbud i Review UI
5. **Forbedring** → Brug Learning UI til at rette kandidater
6. **Verificer** → Søg efter tilbuddene på søgesiden

### Understøttede butikker

| Butik | Status |
|-------|--------|
| Netto | Aktiv |
| Rema 1000 | Aktiv |
| Føtex | Kommer snart |
| Super Brugsen | Kommer snart |
| Spar | Kommer snart |
| 365discount | Kommer snart |

---

## 7. Administration

### Seed standarddata

Hvis systemet er nyt eller data mangler, kan du seed standarddata:

| Handling | Metode |
|----------|--------|
| Opret butikker | `POST /api/management/seed-stores` |
| Opret kategorier | `POST /api/management/seed-categories` |
| Opret standardindstillinger | `POST /api/management/seed-settings` |

> Disse endpoints kræver `x-api-key` header.

### Kategorier

Se og administrer kategorier:

| Handling | Metode |
|----------|--------|
| Se alle kategorier | `GET /api/categories` |
| Opret/opdater kategori | `POST /api/categories` |
| Slet kategori | `DELETE /api/categories/{id}` |
| Ryd kategori-cache | `POST /api/management/clear-category-cache` |

### Indstillinger

| Handling | Metode |
|----------|--------|
| Se alle indstillinger | `GET /api/settings` |
| Se én indstilling | `GET /api/settings/{key}` |
| Opdater indstilling | `PUT /api/settings/{key}` |
| Slet indstilling | `DELETE /api/settings/{key}` |

### Arkivering

Gamle tilbud arkiveres automatisk dagligt. Du kan også trigger manuelt:

| Handling | Metode |
|----------|--------|
| Trigger arkivering | `POST /api/management/trigger-archive` |
| Se arkiverede tilbud | `GET /api/archive/offers` |

### Retailer Rules (scanner-regler)

| Handling | Metode |
|----------|--------|
| Se regler for butik | `GET /api/rules/{retailer}` |
| Opret regel | `POST /api/rules` |
| Opdater regel | `PUT /api/rules/{retailer}/{id}` |
| Slet regel | `DELETE /api/rules/{retailer}/{id}` |

### SKU Overrides

| Handling | Metode |
|----------|--------|
| Se overrides for butik | `GET /api/sku-overrides/{retailer}` |
| Opret override | `POST /api/sku-overrides` |
| Slet override | `DELETE /api/sku-overrides/{retailer}/{id}` |

### Produkt-aliaser (prissammenligning)

| Handling | Metode |
|----------|--------|
| Se alle aliaser | `GET /api/product-aliases` |
| Opret alias-gruppe | `POST /api/product-aliases` |
| Tilføj medlem | `PUT /api/product-aliases/{id}/members` |
| Fjern medlem | `DELETE /api/product-aliases/{id}/members/{retailer}/{skuKeyHash}` |
| Foreslå gruppering | `GET /api/product-alias-suggest` |
| Opslag via alias | `GET /api/product-alias-resolve` |

---

## 8. Rate Limiting

API'en har en grænse på **100 forespørgsler per minut** per IP-adresse. Hvis grænsen overskrides, returneres HTTP 429 (Too Many Requests) med en `Retry-After: 60` header.

---

## 9. Fejlfinding

| Problem | Løsning |
|---------|---------|
| PDF ikke behandlet | Tjek at filnavnet følger formatet `{butik}_{år}-uge{uge}.pdf`. Se også `failed/` containeren i Blob Storage. |
| Ingen tilbud vist | Tjek at indkøbsdatoen falder inden for tilbuddets gyldighedsperiode. |
| Lav konfidens | Brug Review UI og Learning UI til at rette og forbedre. |
| API returnerer 429 | Vent 60 sekunder og prøv igen. |
| Diagnostik viser advarsler | Tjek at GPT og Document Intelligence er korrekt konfigureret i App Settings. |
