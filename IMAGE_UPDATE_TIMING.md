# Image Update Timing Configuration

## Overview

This implementation adds control over **when and how** Docker module images are updated in Azure IoT Edge. Instead of immediately pulling and applying new images when a deployment is received, administrators can now configure when updates should occur.

## Update Modes

### 1. **immediate** (Default)
Images are pulled and applied as soon as they're available in the deployment.

```
Behavior: Traditional behavior (pull immediately, apply immediately)
Use case: Development, testing, always-latest requirements
```

### 2. **on_restart** (Recommended for production)
Images are pulled immediately but applied only when the module restarts.

```
Behavior:
- New deployment received → Image is pulled immediately in background
- Module continues running with old image
- Module restarts (manually or via restart policy) → New image already available, applied on next cycle
- Subsequent deployments with same image → Already have image, applies update

Use case: Minimize downtime windows, batch updates with planned restarts
```

### 3. **scheduled** (For planned maintenance windows)
Images are pulled immediately but applied only within a specified time window each day.

```
Behavior:
- New deployment received → Image is pulled immediately in background
- Outside scheduled window → Module continues with old image
- At scheduled time ± 5 minutes → New image already available, applied automatically

Use case: Scheduled maintenance windows, avoiding peak hours
```

### 4. **on_request** (For manual control)
Images are pulled immediately but applied only when explicitly requested via message.

```
Behavior:
- New deployment received → Image is pulled immediately in background
- No request → Module continues with old image
- Request message sent to edgeHub → Module update is triggered, new image already available

Use case: Manual control, coordinated multi-module updates
```

## Environment Variables

Configure update behavior by setting environment variables in your module deployment manifest.

### IMAGE_UPDATE_MODE
Specifies the update timing strategy.

```json
{
  "env": {
    "IMAGE_UPDATE_MODE": {
      "value": "on_restart"
    }
  }
}
```

**Valid values:** `immediate` (default), `on_restart`, `scheduled`, `on_request`

### IMAGE_UPDATE_SCHEDULE
Required for `scheduled` mode. Specifies the time window (24-hour HH:mm format).

```json
{
  "env": {
    "IMAGE_UPDATE_MODE": {
      "value": "scheduled"
    },
    "IMAGE_UPDATE_SCHEDULE": {
      "value": "23:00"
    }
  }
}
```

**Format:** `HH:mm` in 24-hour format (00:00-23:59)  
**Behavior:** Image updates allowed within ±5 minutes of specified time

## Examples

### Example 1: Production Module with On-Restart Updates

```json
{
  "modules": {
    "myModule": {
      "version": "1.0",
      "type": "docker",
      "status": "running",
      "restartPolicy": "on-failure",
      "settings": {
        "image": "myregistry.azurecr.io/mymodule:latest",
        "createOptions": "{}"
      },
      "env": {
        "IMAGE_UPDATE_MODE": {
          "value": "on_restart"
        }
      }
    }
  }
}
```

**Result:**
- New image is pulled immediately in background
- Module continues running with current image
- When module restarts (or restarts due to failure), new image is already available and applied
- Minimizes unexpected downtime

### Example 2: Maintenance Window Updates

```json
{
  "modules": {
    "criticalService": {
      "version": "1.0",
      "type": "docker",
      "status": "running",
      "restartPolicy": "on-failure",
      "settings": {
        "image": "myregistry.azurecr.io/criticalservice:latest",
        "createOptions": "{}"
      },
      "env": {
        "IMAGE_UPDATE_MODE": {
          "value": "scheduled"
        },
        "IMAGE_UPDATE_SCHEDULE": {
          "value": "02:00"
        }
      }
    }
  }
}
```

**Result:**
- New image is pulled immediately in background
- Every day at 02:00 (±5 minutes), if a new image is available, it's applied
- Outside this window, module uses current image
- Perfect for data centers with off-peak hours

### Example 3: No Update Mode (Keep Current Image)

```json
{
  "env": {
    "IMAGE_UPDATE_MODE": {
      "value": "on_request"
    }
  }
}
```

**Result:**
- New image is pulled immediately in background
- Module continues indefinitely with current image
- No automatic apply occurs
- Apply only happens when explicitly requested

## Triggering Updates in `on_request` Mode

There are two ways to trigger updates for modules configured with `IMAGE_UPDATE_MODE=on_request`:

### Method 1: Direct Method (Local or Cloud)

Call the `TriggerModuleUpdate` direct method on `$edgeAgent`:

**From Azure CLI:**
```bash
az iot hub invoke-module-method \
  --hub-name <your-hub> \
  --device-id <your-device> \
  --module-id '$edgeAgent' \
  --method-name TriggerModuleUpdate \
  --method-payload '{"moduleName":"myModule"}'
```

**From a local module (C#):**
```csharp
using Microsoft.Azure.Devices.Client;

var moduleClient = await ModuleClient.CreateFromEnvironmentAsync();
var method = new MethodRequest(
    "TriggerModuleUpdate",
    Encoding.UTF8.GetBytes("{\"moduleName\":\"myModule\"}")
);
var response = await moduleClient.InvokeMethodAsync("$edgeAgent", method);
Console.WriteLine($"Status: {response.Status}, Result: {response.ResultAsJson}");
```

**From IoT Hub:**
Direct method invocation from IoT Hub can be done via Azure Portal, targeting the `$edgeAgent` module with the same payload.
Method name: `TriggerModuleUpdate`  
Payload:
```json
{
  "moduleName": "myModule"
}
```

**Response:**
```json
{
  "moduleName": "myModule",
  "message": "Update request registered. Module will be updated on next reconciliation cycle."
}
```

### Method 2: Desired Properties (Recommended for Testing)

Update the `$edgeAgent` module twin desired properties from Azure Portal or Azure CLI:

**From Azure Portal:**
1. Navigate to IoT Hub → Devices → Your Device
2. Click on `$edgeAgent` module
3. Go to "Module Identity Twin"
4. Add or update the `updateTriggers` property in desired:

```json
{
  "properties": {
    "desired": {
      "updateTriggers": {
        "myModule": {
          "requestUpdate": true,
          "timestamp": "2026-06-04T12:00:00Z"
        }
      }
    }
  }
}
```

**From Azure CLI:**
```bash
az iot hub module-twin update \
  --hub-name <your-hub> \
  --device-id <your-device> \
  --module-id '$edgeAgent' \
  --desired '{
    "updateTriggers": {
      "myModule": {
        "requestUpdate": true,
        "timestamp": "2026-06-04T12:00:00Z"
      }
    }
  }'
```

**EdgeAgent will acknowledge the trigger in reported properties:**
```json
{
  "properties": {
    "reported": {
      "updateTriggers": {
        "myModule": {
          "requestUpdate": true,
          "timestamp": "2026-06-04T12:00:00Z",
          "acknowledged": "2026-06-04T12:00:15Z",
          "status": "pending"
        }
      }
    }
  }
}
```

### Comparison: Direct Method vs Desired Properties

| Feature | Direct Method | Desired Properties |
|---------|--------------|-------------------|
| **Response** | Immediate (synchronous) | Asynchronous |
| **Triggering from** | Cloud or local module | Cloud or edgeHub API |
| **Testing** | Requires script/CLI | Easy via Azure Portal |
| **Audit trail** | Request logs only | Visible in twin history |
| **Feedback** | JSON response | Reported properties updated |
| **Use case** | Automated triggers, local orchestration | Manual testing, cloud orchestration |

## Implementation Details

### Architecture

The timing control is implemented via:

1. **UpdateScheduleManager** - Core logic for determining if/when updates should occur
2. **HealthRestartPlanner modifications** - Integrates schedule manager into planning phase
3. **Environment variable parsing** - Extracts configuration from module Env

### Processing Flow

```
Deployment Received
    ↓
HealthRestartPlanner.PlanAsync()
    ↓
ProcessAddedUpdatedModules()
    ├─ For each module:
    │   ├─ Ask UpdateScheduleManager: "Should prepare (pull)?"
    │   │   ├─ If image is already present → false (skip pull, already up to date)
    │   │   └─ Otherwise → true (pull immediately, regardless of mode)
    │   ├─ If NO → PrepareUpdateCommand becomes NullCommand (not executed)
    │   ├─ Ask UpdateScheduleManager: "Should apply?"
    │   │   └─ Returns: true/false based on IMAGE_UPDATE_MODE + current state
    │   ├─ If NO → CreateOrUpdateCommand becomes NullCommand (not executed)
    │   └─ NullCommands are filtered out and not executed
    ↓
Commands Executed (pull always happens if image is new; apply only if mode allows)
```

### State Tracking

- **Last Update Attempt**: Prevents repeated updates within same hour for scheduled mode
- **Runtime Module Status**: Checks if module is currently running
- **Module Restart Detection**: Monitors LastStartTimeUtc to detect recent restarts (within 1 minute)

## Behavior by Mode and Condition

| Mode | Image present? | Module Running | Condition | Prepare (pull)? | Apply? | Note |
|------|---|---|---|---|---|---|
| **immediate** | No | Yes/No | - | ✓ | ✓ | Pull and apply immediately |
| **immediate** | Yes | Yes/No | - | ✗ | ✓ | Already pulled, apply immediately |
| **on_restart** | No | Yes | - | ✓ | ✗ | Pull now, wait for restart to apply |
| **on_restart** | Yes | Yes | - | ✗ | ✗ | Already pulled, wait for restart |
| **on_restart** | Yes | No | Restarting | ✗ | ✓ | Apply on restart |
| **scheduled** | No | Any | Any | ✓ | ✗ outside window / ✓ in window | Pull now, apply at scheduled time |
| **scheduled** | Yes | Any | Within window | ✗ | ✓ | Already pulled, apply |
| **scheduled** | Yes | Any | Outside window | ✗ | ✗ | Wait for window |
| **on_request** | No | Any | Any | ✓ | ✗ no request / ✓ requested | Pull now, apply on request |
| **on_request** | Yes | Any | Request sent | ✗ | ✓ | Already pulled, apply |
| **on_request** | Yes | Any | No request | ✗ | ✗ | Wait for request |

## Use Cases

### Edge Device with Limited Bandwidth
```
Use: on_restart mode
Reason: Image is pre-downloaded in background; switch happens only at planned restart (e.g. at night)
```

### Critical Production Service
```
Use: scheduled mode at 02:00-04:00
Reason: Known maintenance window, predictable, planned downtime
```

### Development Gateway
```
Use: immediate mode (default)
Reason: Always have latest changes for testing
```

### Multi-Module Coordinated Update
```
Use: on_request mode with orchestrator
Reason: Update multiple modules in sequence, coordinated via messages
```

## Error Handling

- **Invalid IMAGE_UPDATE_MODE**: Defaults to `immediate` with warning logged
- **Invalid IMAGE_UPDATE_SCHEDULE format**: Falls back to `no update` for that cycle with error logged
- **Missing schedule for scheduled mode**: Logs warning, skips updates for that module
- **Runtime errors**: Defaults to `immediate` to avoid blocking updates

## Future Enhancements

1. ✅ **Direct method** on edgeAgent to trigger on_request updates (Implemented in v1.6.1)
2. ✅ **Desired properties** support for triggering updates (Implemented in v1.6.1)
3. ✅ **Update status reporting** in module twins via reported properties (Implemented in v1.6.1)
4. **Cron-like scheduling** with more complex time patterns
5. **Rate limiting** for scheduled mode (e.g., max 1 update per day)
6. **Rollback triggers** if module health degrades after update
7. **MQTT message routing** for triggering updates from other modules
