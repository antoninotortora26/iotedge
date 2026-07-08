# Guida al Rilascio dell'Immagine Edge Agent Customizzata

Questa guida descrive come creare e rilasciare una nuova versione dell'immagine Docker per Azure IoT Edge Agent con le personalizzazioni sviluppate (UpdateScheduleManager, Direct Method trigger, Desired Properties trigger).

## 📋 Prerequisiti

### Software Richiesto
- **Docker Desktop** - Avviato e funzionante
- **.NET 8.0 SDK** - Per compilare il codice C#
- **Git per Windows** - Include Git Bash necessario per gli script Microsoft
- **Azure CLI** - Per l'autenticazione al Container Registry

### Configurazione Iniziale
```powershell
# Verificare Docker
docker --version

# Verificare .NET
dotnet --version

# Verificare Git Bash
Test-Path "$env:LOCALAPPDATA\Programs\Git\bin\bash.exe"

# Login ad Azure Container Registry
az acr login --name <container_registry_name>
```

## 🚀 Processo di Rilascio Completo

### Metodo 1: Script Microsoft Ufficiali (Raccomandato)

Questo metodo usa gli script bash ufficiali di Microsoft tramite Git Bash.

#### Step 1: Preparazione librocksdb.so

La libreria RocksDB è necessaria per il funzionamento dell'Edge Agent. Estraiamola dall'immagine ufficiale Microsoft:

```powershell
# Naviga nella root del repository
cd $env:USERPROFILE\Documents\GitSource\iotedge

# Crea la struttura delle directory
mkdir -p edge-agent/docker/linux/librocksdb/linux/amd64

# Estrai librocksdb.so dall'immagine ufficiale Microsoft
docker pull mcr.microsoft.com/azureiotedge-agent:1.5
docker create --name temp-agent mcr.microsoft.com/azureiotedge-agent:1.5
docker cp temp-agent:/usr/local/lib/librocksdb.so edge-agent/docker/linux/librocksdb/linux/amd64/librocksdb.so
docker rm temp-agent
```

**Nota**: Questo step va fatto **solo la prima volta** o quando si aggiorna la versione di RocksDB.

#### Step 2: Build dei Binari con buildBranch.sh

```powershell
# Naviga nella directory degli script
cd scripts/linux

# Esegui buildBranch.sh con Git Bash
& "$env:LOCALAPPDATA\Programs\Git\bin\bash.exe" ./buildBranch.sh --config Release

# Ritorna alla root
cd ../..
```

Questo script:
- Compila tutti i progetti in configurazione Release
- Pubblica i binari in `target/publish/Microsoft.Azure.Devices.Edge.Agent.Service/`
- Copia automaticamente il Dockerfile e le dipendenze necessarie

**Output atteso**: `Build succeeded` per tutti i progetti

#### Step 3: Copia librocksdb nella directory di publish

Gli script Microsoft si aspettano che `librocksdb` sia sia in `docker/linux/` che nella root dell'app:

```powershell
# Copia librocksdb dalla sottodirectory docker alla radice dell'app
Copy-Item -Recurse `
  target/publish/Microsoft.Azure.Devices.Edge.Agent.Service/docker/linux/librocksdb `
  target/publish/Microsoft.Azure.Devices.Edge.Agent.Service/librocksdb
```

#### Step 4: Build dell'Immagine Docker con buildImage.sh

```powershell
# Definisci le variabili
$VERSION = "1.5.1"  # ⚠️ AGGIORNA QUESTA VERSIONE AD OGNI RILASCIO
$REGISTRY = "<container_registry_name>.azurecr.io"
$IMAGE_NAME = "azureiotedge-agent-custom"

# Naviga nella directory degli script
cd scripts/linux

# Esegui buildImage.sh con Git Bash
& "$env:LOCALAPPDATA\Programs\Git\bin\bash.exe" ./buildImage.sh `
  --app "Microsoft.Azure.Devices.Edge.Agent.Service" `
  --bin "../../target" `
  --name "$IMAGE_NAME" `
  --registry "$REGISTRY" `
  --version "$VERSION" `
  --platforms "linux/amd64"

# Ritorna alla root
cd ../..
```

**Nota**: Per build multi-architettura (amd64, arm64, arm32v7), cambia `--platforms "linux/amd64,linux/arm64,linux/arm/v7"`

#### Step 5: Verifica e Push dell'Immagine

```powershell
# Verifica l'immagine creata
docker images $REGISTRY/$IMAGE_NAME:$VERSION --format "table {{.Repository}}:{{.Tag}}\t{{.Size}}"

# Push al registry
docker push $REGISTRY/$IMAGE_NAME:$VERSION
```

---

### Metodo 2: Comandi Diretti PowerShell (Alternativo)

Se Git Bash non è disponibile o preferisci non usare gli script bash, puoi eseguire i comandi direttamente.

#### Step 1: Preparazione librocksdb.so

```powershell
# (Stesso dello Step 1 del Metodo 1)
cd $env:USERPROFILE\Documents\GitSource\iotedge
mkdir -p edge-agent/docker/linux/librocksdb/linux/amd64
docker pull mcr.microsoft.com/azureiotedge-agent:1.5
docker create --name temp-agent mcr.microsoft.com/azureiotedge-agent:1.5
docker cp temp-agent:/usr/local/lib/librocksdb.so edge-agent/docker/linux/librocksdb/linux/amd64/librocksdb.so
docker rm temp-agent
```

#### Step 2: Build e Publish con dotnet

```powershell
# Build in Release (opzionale, publish lo fa automaticamente)
dotnet build -c Release `
  edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.Service/Microsoft.Azure.Devices.Edge.Agent.Service.csproj

# Publish senza simboli di debug per ridurre dimensione
dotnet publish -c Release `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  edge-agent/src/Microsoft.Azure.Devices.Edge.Agent.Service/Microsoft.Azure.Devices.Edge.Agent.Service.csproj `
  -o target/publish/Microsoft.Azure.Devices.Edge.Agent.Service
```

#### Step 3: Copia librocksdb

```powershell
Copy-Item -Recurse `
  target/publish/Microsoft.Azure.Devices.Edge.Agent.Service/docker/linux/librocksdb `
  target/publish/Microsoft.Azure.Devices.Edge.Agent.Service/librocksdb
```

#### Step 4: Build Docker con buildx

```powershell
$VERSION = "1.5.1"  # ⚠️ AGGIORNA VERSIONE
$REGISTRY = "<container_registry_name>.azurecr.io"
$IMAGE_NAME = "azureiotedge-agent-custom"

docker buildx build `
  --platform linux/amd64 `
  --tag "$REGISTRY/${IMAGE_NAME}:$VERSION" `
  --file target/publish/Microsoft.Azure.Devices.Edge.Agent.Service/docker/linux/Dockerfile `
  --load `
  target/publish/Microsoft.Azure.Devices.Edge.Agent.Service/
```

#### Step 5: Push

```powershell
docker push "$REGISTRY/${IMAGE_NAME}:$VERSION"
```

---

## 📝 Versionamento

Segui il semantic versioning `MAJOR.MINOR.PATCH`:

- **MAJOR**: Cambiamenti incompatibili con versioni precedenti
- **MINOR**: Nuove funzionalità retrocompatibili
- **PATCH**: Bug fix retrocompatibili

### Versioni Rilasciate

| Versione | Data       | Modifiche                                                           |
|----------|------------|---------------------------------------------------------------------|
| 1.6.1    | 2026-06-04 | Prima versione con UpdateScheduleManager, Direct Method e Twin      |
| 1.7.0    | 2026-07-08 | Build con processo Microsoft ufficiale, rimozione simboli debug     |

**Prossima versione suggerita**: 1.7.1

---

## 🔍 Verifica del Rilascio

### Test Locale dell'Immagine

```powershell
# Avvia un container di test
docker run -it --rm `
  -e "IMAGE_UPDATE_MODE=on_restart" `
  "$REGISTRY/${IMAGE_NAME}:$VERSION" `
  /bin/sh

# Verifica i file
ls -la /app/
ls -la /usr/local/lib/librocksdb.so
```

### Verifica nel Registry

```powershell
# Lista le tag disponibili
az acr repository show-tags `
  --name cnrdwfweuts001 `
  --repository $IMAGE_NAME `
  --output table

# Mostra i dettagli dell'immagine
az acr repository show `
  --name cnrdwfweuts001 `
  --repository $IMAGE_NAME `
  --output table
```

---

## 🛠️ Troubleshooting

### Problema: "authentication required" durante docker push

**Soluzione**:
```powershell
az acr login --name cnrdwfweuts001
```

### Problema: buildBranch.sh errore "/c: Is a directory"

**Causa**: Git Bash ha problemi con il path di dotnet su Windows.

**Soluzione**: Usa il Metodo 2 (comandi PowerShell diretti) oppure ignora i warning se il build completa con successo.

### Problema: packages.lock.json modificati dopo dotnet restore

**Causa**: StyleCop.Analyzers è incluso solo in configurazione Release (vedi `stylecop.props`).

**Soluzione**: 
```powershell
# Ripristina i file originali
git checkout -- **/*.csproj **/*packages.lock.json Microsoft.Azure.Devices.Edge.sln

# Usa sempre --config Release nei build
```

### Problema: librocksdb.so non trovato nel container

**Causa**: La directory librocksdb non è stata copiata correttamente.

**Soluzione**: Verifica che esista `target/publish/Microsoft.Azure.Devices.Edge.Agent.Service/librocksdb/linux/amd64/librocksdb.so`

---

## 📚 Riferimenti

- [Microsoft IoT Edge DevGuide](doc/devguide.md)
- [IMAGE_UPDATE_TIMING.md](IMAGE_UPDATE_TIMING.md) - Documentazione delle modalità di update
- [Dockerfile Ufficiale](edge-agent/docker/linux/Dockerfile)
- [buildBranch.sh](scripts/linux/buildBranch.sh)
- [buildImage.sh](scripts/linux/buildImage.sh)

---

## ✅ Checklist Pre-Rilascio

- [ ] Codice committato e pushato su Git
- [ ] Versione aggiornata in questa guida
- [ ] Docker Desktop avviato
- [ ] Login ad Azure Container Registry effettuato
- [ ] librocksdb.so estratto (se prima volta)
- [ ] Build completata con successo
- [ ] Immagine Docker creata
- [ ] Immagine testata localmente
- [ ] Push al registry completato
- [ ] Versione aggiornata nella tabella "Versioni Rilasciate"
- [ ] Deployment manifest aggiornato con la nuova versione

---

## 🎯 Quick Reference - Rilascio Rapido

```powershell
# 1. Setup (solo prima volta)
cd $env:USERPROFILE\Documents\GitSource\iotedge
docker pull mcr.microsoft.com/azureiotedge-agent:1.5
docker create --name temp-agent mcr.microsoft.com/azureiotedge-agent:1.5
docker cp temp-agent:/usr/local/lib/librocksdb.so edge-agent/docker/linux/librocksdb/linux/amd64/librocksdb.so
docker rm temp-agent

# 2. Build (ogni rilascio)
cd scripts/linux
& "$env:LOCALAPPDATA\Programs\Git\bin\bash.exe" ./buildBranch.sh --config Release
cd ../..
Copy-Item -Recurse target/publish/Microsoft.Azure.Devices.Edge.Agent.Service/docker/linux/librocksdb target/publish/Microsoft.Azure.Devices.Edge.Agent.Service/librocksdb

# 3. Docker Build & Push
$VERSION = "1.7.1"  # ⚠️ AGGIORNA!
docker buildx build --platform linux/amd64 --tag "cnrdwfweuts001.azurecr.io/azureiotedge-agent-ava:$VERSION" --file target/publish/Microsoft.Azure.Devices.Edge.Agent.Service/docker/linux/Dockerfile --load target/publish/Microsoft.Azure.Devices.Edge.Agent.Service/
docker push "cnrdwfweuts001.azurecr.io/azureiotedge-agent-ava:$VERSION"
```
