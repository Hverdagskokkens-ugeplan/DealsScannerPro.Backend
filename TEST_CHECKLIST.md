# DealsScannerPro - Test Checklist

Brug denne checklist til at verificere at hele systemet fungerer korrekt.

---

## 1. Grundlæggende sundhedstjek

- [ ] Søgesiden loader: https://stdealscannerprod.z6.web.core.windows.net/
- [ ] Diagnostik-siden loader: https://stdealscannerprod.z6.web.core.windows.net/diagnostics.html
- [ ] GPT og Document Intelligence vises som aktive i diagnostik
- [ ] API svarer: https://func-dealscanner-prod.azurewebsites.net/api/stores
- [ ] Butikslisten indeholder Netto, Rema 1000, m.fl.

## 2. Upload tilbudsavis (end-to-end)

- [ ] Hent en aktuel Netto eller Rema tilbudsavis (PDF)
- [ ] Upload til blob container `tilbudsaviser` med korrekt navn (f.eks. `netto_2026-uge07.pdf`)
- [ ] Vent 2-3 minutter
- [ ] PDF er flyttet til `processed/` mappen
- [ ] PDF er IKKE i `failed/` mappen
- [ ] Tjek scanner Function App logs i Application Insights for fejl

## 3. Tilbud er synlige

- [ ] Søg efter et produkt du ved er i avisen på søgesiden
- [ ] Resultater vises med korrekt pris
- [ ] Filtrering på butik virker
- [ ] Filtrering på dato virker
- [ ] `/api/deals?butik=netto` returnerer tilbud
- [ ] `/api/deals/{id}` returnerer et enkelt tilbud (brug et ID fra ovenstående)

## 4. Datakvalitet

- [ ] Produktnavne er korrekte (ikke garbled tekst)
- [ ] Priser er fornuftige (ikke 0 eller ekstremt høje)
- [ ] Kategorier giver mening (f.eks. kylling → Kød, mælk → Mejeri)
- [ ] Gyldighedsperiode (gyldig fra/til) er korrekt
- [ ] Mængde og enhed er udfyldt på de fleste tilbud

## 5. Review UI

- [ ] Åbn https://stdealscannerprod.z6.web.core.windows.net/review.html
- [ ] Log ind med demo-kode: `demo123`
- [ ] Review-kø vises med tilbud der har lav konfidens
- [ ] Godkend ét tilbud — status ændres
- [ ] Afvis ét tilbud — status ændres
- [ ] Rediger ét tilbud (ret navn eller pris) — ændring gemmes
- [ ] Batch-godkend flere tilbud — alle ændres
- [ ] Filtrering på butik virker
- [ ] Stats-bar viser korrekte tal (afventer, godkendt, afvist)

## 6. Learning UI

- [ ] Åbn https://stdealscannerprod.z6.web.core.windows.net/learning.html
- [ ] Log ind med demo-kode: `demo123`
- [ ] Tilbud med kandidater vises
- [ ] Vælg en pris-kandidat (klikbar chip) — valget gemmes
- [ ] Vælg en mængde-kandidat — valget gemmes
- [ ] Rettelsen er synlig når tilbuddet genindlæses

## 7. Indkøbsliste-matching

- [ ] Send POST til `/api/match-shopping-list` med body:
  ```json
  { "items": ["mælk", "smør", "kylling", "pasta"] }
  ```
- [ ] Svar indeholder matchede tilbud for hver vare
- [ ] Priser og butikker er med i svaret

## 8. Caching og Cache-Control headers

- [ ] `GET /api/stores` har header `Cache-Control: public, max-age=3600`
- [ ] `GET /api/categories` har header `Cache-Control: public, max-age=3600`
- [ ] `GET /api/deals` har header `Cache-Control: public, max-age=300`
- [ ] Gentaget kald til `/api/stores` er hurtigere (cached i 1 time)
- [ ] Gentaget kald til `/api/deals` er hurtigere (cached i 5 min)

## 9. Rate Limiting

- [ ] Send 100+ hurtige forespørgsler til et endpoint
- [ ] Forespørgsel nr. 101 returnerer HTTP 429 (Too Many Requests)
- [ ] Response indeholder `Retry-After: 60` header
- [ ] Response body indeholder fejlbesked i JSON
- [ ] Efter 60 sekunder virker forespørgsler igen

## 10. Administration

- [ ] `GET /api/categories` returnerer kategoriliste
- [ ] `GET /api/settings` returnerer systemindstillinger
- [ ] `GET /api/archive/offers` returnerer arkiverede tilbud (eller tom liste)
- [ ] `GET /api/rules/netto` returnerer retailer-regler (eller tom liste)
- [ ] `GET /api/product-aliases` returnerer produkt-aliaser (eller tom liste)

---

## Når alt er testet

Hvis alle punkter er afkrydset, er systemet klar til produktion.

Hvis der er fejl, noter:
1. Hvilket punkt fejlede
2. Hvad du forventede vs. hvad der skete
3. Evt. fejlbesked eller HTTP statuskode
