# Manual Testing Guide: PDF Upload Pipeline

Denne guide beskriver hvordan du manuelt tester hele PDF upload pipeline'en fra start til slut.

## Forudsætninger

- Azure CLI installeret og logget ind (`az login`)
- Adgang til `stdealscannerprod` storage account

## Oversigt

```
┌─────────────┐     ┌──────────────┐     ┌─────────────┐     ┌──────────────┐
│ Upload PDF  │ --> │ Blob Trigger │ --> │ Scanner     │ --> │ Data i DB    │
│ til blob    │     │ aktiveres    │     │ processor   │     │ (Table)      │
└─────────────┘     └──────────────┘     └─────────────┘     └──────────────┘
```

---

## Trin 1: Slet eksisterende data i database

Først rydder vi Table Storage for at starte på en frisk.

### Se hvor mange deals der er nu

```bash
az storage entity query \
  --table-name Tilbud \
  --account-name stdealscannerprod \
  --query "length(items)"
```

### Slet alle deals

```bash
# Hent alle entries og slet dem én for én
az storage entity query \
  --table-name Tilbud \
  --account-name stdealscannerprod \
  --select PartitionKey,RowKey \
  --num-results 1000 \
  -o json | python -c "
import json, sys, subprocess

data = json.load(sys.stdin)
items = data.get('items', [])
print(f'Sletter {len(items)} entries...')

for i, item in enumerate(items):
    pk = item['PartitionKey']
    rk = item['RowKey']
    subprocess.run([
        'az', 'storage', 'entity', 'delete',
        '--table-name', 'Tilbud',
        '--account-name', 'stdealscannerprod',
        '--partition-key', pk,
        '--row-key', rk,
        '-o', 'none'
    ], capture_output=True)
    if (i + 1) % 50 == 0:
        print(f'  Slettet {i + 1}/{len(items)}')

print('Færdig!')
"
```

### Verificer at tabellen er tom

```bash
az storage entity query \
  --table-name Tilbud \
  --account-name stdealscannerprod \
  --query "length(items)"
```

Forventet output: `0`

---

## Trin 2: Ryd op i blob storage

### Se hvad der ligger i hver container

```bash
# Tilbudsaviser (input - ventende PDFs)
echo "=== tilbudsaviser (ventende) ==="
az storage blob list \
  --container-name tilbudsaviser \
  --account-name stdealscannerprod \
  --query "[].name" -o tsv

# Processed (færdigbehandlede)
echo "=== processed (arkiverede) ==="
az storage blob list \
  --container-name processed \
  --account-name stdealscannerprod \
  --query "[].name" -o tsv

# Failed (fejlede)
echo "=== failed (fejlede) ==="
az storage blob list \
  --container-name failed \
  --account-name stdealscannerprod \
  --query "[].name" -o tsv
```

### Slet alle blobs i tilbudsaviser

```bash
# List og slet hver blob
az storage blob list \
  --container-name tilbudsaviser \
  --account-name stdealscannerprod \
  --query "[].name" -o tsv | while read blob; do
    echo "Sletter: $blob"
    az storage blob delete \
      --container-name tilbudsaviser \
      --account-name stdealscannerprod \
      --name "$blob" -o none
done
```

### (Valgfrit) Slet blobs i processed/failed

```bash
# Processed
az storage blob list \
  --container-name processed \
  --account-name stdealscannerprod \
  --query "[].name" -o tsv | while read blob; do
    az storage blob delete \
      --container-name processed \
      --account-name stdealscannerprod \
      --name "$blob" -o none
done

# Failed
az storage blob list \
  --container-name failed \
  --account-name stdealscannerprod \
  --query "[].name" -o tsv | while read blob; do
    az storage blob delete \
      --container-name failed \
      --account-name stdealscannerprod \
      --name "$blob" -o none
done
```

---

## Trin 3: Upload PDF til blob storage

### Filnavn format

PDFs skal navngives efter dette mønster:
```
{butik}_{år}-uge{ugenummer}.pdf
```

Eksempler:
- `netto_2025-uge50.pdf`
- `rema_2025-uge51.pdf`
- `foetex_2025-uge50.pdf`

### Upload en PDF

```bash
az storage blob upload \
  --container-name tilbudsaviser \
  --account-name stdealscannerprod \
  --name "netto_2025-uge50.pdf" \
  --file "/sti/til/din/netto_2025-uge50.pdf" \
  --overwrite
```

### Verificer upload

```bash
az storage blob list \
  --container-name tilbudsaviser \
  --account-name stdealscannerprod \
  --query "[].{name:name, size:properties.contentLength}" -o table
```

---

## Trin 4: Test at blob trigger er kørt

Blob triggeren kører automatisk når en fil uploades. Vent 1-2 minutter og tjek derefter.

### Tjek om PDF er flyttet fra tilbudsaviser

```bash
# Hvis tom, er PDF'en blevet processeret
az storage blob list \
  --container-name tilbudsaviser \
  --account-name stdealscannerprod \
  --query "[].name" -o tsv
```

### Tjek om PDF er i processed

```bash
az storage blob list \
  --container-name processed \
  --account-name stdealscannerprod \
  --query "[?contains(name, 'netto_2025-uge50')].name" -o tsv
```

### Hvis trigger ikke kører

Prøv at synkronisere function triggers:

```bash
# Find subscription ID
SUB_ID=$(az account show --query id -o tsv)

# Sync triggers
az rest --method POST \
  --url "/subscriptions/$SUB_ID/resourceGroups/rg-dealscanner-prod/providers/Microsoft.Web/sites/func-dealscanner-scanner-prod/syncfunctiontriggers?api-version=2022-03-01"

# Genstart function app
az functionapp restart \
  --name func-dealscanner-scanner-prod \
  --resource-group rg-dealscanner-prod
```

Vent 30 sekunder og upload PDF'en igen.

---

## Trin 5: Tjek for fejl

### Se om PDF landede i failed container

```bash
az storage blob list \
  --container-name failed \
  --account-name stdealscannerprod \
  --query "[].name" -o tsv
```

### Se fejl-metadata på failed blob

```bash
az storage blob metadata show \
  --container-name failed \
  --account-name stdealscannerprod \
  --name "2025/uge51/netto_2025-uge50.pdf"
```

### Tjek function logs (Application Insights)

```bash
# Åbn Azure Portal og gå til:
# func-dealscanner-scanner-prod -> Monitor -> Logs

# Eller brug denne query i Log Analytics:
# traces | where timestamp > ago(30m) | order by timestamp desc
```

---

## Trin 6: Se data i database

### Simpel count

```bash
az storage entity query \
  --table-name Tilbud \
  --account-name stdealscannerprod \
  --query "length(items)"
```

### Se de første 10 deals

```bash
az storage entity query \
  --table-name Tilbud \
  --account-name stdealscannerprod \
  --num-results 10 \
  --query "items[].{Produkt:Produkt, Pris:TotalPris, Butik:Butik, Konfidens:Konfidens}" \
  -o table
```

### Gruppér efter butik og uge

```bash
az storage entity query \
  --table-name Tilbud \
  --account-name stdealscannerprod \
  --num-results 500 \
  -o json | python -c "
import json, sys
data = json.load(sys.stdin)
items = data.get('items', [])

# Gruppér efter PartitionKey
groups = {}
for item in items:
    pk = item.get('PartitionKey', 'unknown')
    konf = float(item.get('Konfidens', 0))
    if pk not in groups:
        groups[pk] = {'total': 0, 'high_conf': 0}
    groups[pk]['total'] += 1
    if konf >= 0.8:
        groups[pk]['high_conf'] += 1

print(f'Total deals: {len(items)}\n')
for pk, data in sorted(groups.items()):
    pct = data['high_conf']/data['total']*100 if data['total'] else 0
    print(f\"{pk}: {data['total']} deals, {data['high_conf']} high conf ({pct:.0f}%)\")
"
```

### Brug API'et

```bash
curl -s "https://func-dealscanner-prod.azurewebsites.net/api/tilbud" | python -m json.tool | head -50
```

---

## Hurtig test-cyklus (alle trin)

Her er et komplet script til at køre hele flowet:

```bash
#!/bin/bash
set -e

PDF_FILE="$1"
if [ -z "$PDF_FILE" ]; then
    echo "Usage: $0 <pdf-file>"
    exit 1
fi

PDF_NAME=$(basename "$PDF_FILE")
ACCOUNT="stdealscannerprod"

echo "=== 1. Sletter eksisterende data ==="
# (Spring over for hurtig test, eller kør delete-scriptet)

echo "=== 2. Uploader $PDF_NAME ==="
az storage blob upload \
  --container-name tilbudsaviser \
  --account-name $ACCOUNT \
  --name "$PDF_NAME" \
  --file "$PDF_FILE" \
  --overwrite -o none

echo "=== 3. Venter på blob trigger (60 sek) ==="
sleep 60

echo "=== 4. Tjekker status ==="
REMAINING=$(az storage blob list --container-name tilbudsaviser --account-name $ACCOUNT --query "[?name=='$PDF_NAME'].name" -o tsv)
if [ -z "$REMAINING" ]; then
    echo "✓ PDF processeret (ikke i tilbudsaviser)"
else
    echo "✗ PDF stadig i tilbudsaviser - trigger ikke kørt?"
fi

PROCESSED=$(az storage blob list --container-name processed --account-name $ACCOUNT --query "[?contains(name,'$PDF_NAME')].name" -o tsv)
if [ -n "$PROCESSED" ]; then
    echo "✓ PDF i processed: $PROCESSED"
fi

FAILED=$(az storage blob list --container-name failed --account-name $ACCOUNT --query "[?contains(name,'$PDF_NAME')].name" -o tsv)
if [ -n "$FAILED" ]; then
    echo "✗ PDF i failed: $FAILED"
fi

echo "=== 5. Deals i database ==="
az storage entity query \
  --table-name Tilbud \
  --account-name $ACCOUNT \
  --num-results 500 \
  -o json | python -c "
import json, sys
data = json.load(sys.stdin)
items = data.get('items', [])
high = sum(1 for i in items if float(i.get('Konfidens',0)) >= 0.8)
print(f'Total: {len(items)} deals, {high} high confidence ({high/len(items)*100:.0f}%)' if items else 'Ingen deals')
"

echo "=== Færdig! ==="
```

Gem som `test-pipeline.sh` og kør:
```bash
chmod +x test-pipeline.sh
./test-pipeline.sh /sti/til/netto_2025-uge50.pdf
```

---

## Fejlfinding

| Problem | Løsning |
|---------|---------|
| PDF bliver ikke processeret | Kør `syncfunctiontriggers` og genstart function app |
| PDF lander i failed | Tjek metadata på blob og logs i App Insights |
| Ingen deals i database | Tjek at API key er konfigureret i scanner function |
| Lav konfidens | PDF'en matcher muligvis ikke scanner-mønstrene |
