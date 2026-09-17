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

## Configuration

Configure update behavior by setting environment variables in your deployment manifest:

### Module-Specific Configuration

Set `IMAGE_UPDATE_MODE` and `IMAGE_UPDATE_SCHEDULE` on individual modules to control their update behavior.

### IMAGE_UPDATE_MODE
Specifies the update timing strategy for a specific module.

```json
{
  "modules": {
    "myModule": {
      "env": {
        "IMAGE_UPDATE_MODE": {
          "value": "on_restart"
        }
      }
    }
  }
}
```

**Valid values:** `immediate` (default), `on_restart`, `scheduled`, `on_request`

### IMAGE_UPDATE_SCHEDULE
Required for `scheduled` mode on a specific module. Supports two formats: a daily recurring time window, or a one-time absolute date/time.

**Format 1 — daily recurring `HH:mm` (24-hour format, 00:00-23:59):**
```json
{
  "modules": {
    "myModule": {
      "env": {
        "IMAGE_UPDATE_MODE": {
          "value": "scheduled"
        },
        "IMAGE_UPDATE_SCHEDULE": {
          "value": "23:00"
        }
      }
    }
  }
}
```
**Behavior:** Image updates allowed within ±5 minutes of the specified time, every day.

**Format 2 — full date/time (ISO 8601, one-time absolute schedule):**
```json
{
  "modules": {
    "myModule": {
      "env": {
        "IMAGE_UPDATE_MODE": {
          "value": "scheduled"
        },
        "IMAGE_UPDATE_SCHEDULE": {
          "value": "2026-09-20T23:00:00"
        }
      }
    }
  }
}
```
**Behavior:** Update applied once the current date/time reaches the specified value; no repeating window.

### Default Configuration on $edgeAgent

Set `DEFAULT_IMAGE_UPDATE_MODE` and `DEFAULT_IMAGE_UPDATE_SCHEDULE` on **$edgeAgent** to specify default behavior for all modules that don't have their own `IMAGE_UPDATE_MODE` configured.

```json
{
  "$edgeAgent": {
    "properties.desired": {
      "systemModules": {
        "edgeAgent": {
          "env": {
            "DEFAULT_IMAGE_UPDATE_MODE": {
              "value": "on_restart"
            },
            "DEFAULT_IMAGE_UPDATE_SCHEDULE": {
              "value": "02:00"
            }
          }
        }
      }
    }
  }
}
```

**Configuration Priority:**

1. **Module-specific** `IMAGE_UPDATE_MODE` (highest priority)
2. **Default from $edgeAgent** `DEFAULT_IMAGE_UPDATE_MODE`
3. **Hardcoded default**: `immediate`



## Examples

### Example 1: Production Module with On-Restart Updates

**With default configuration on $edgeAgent:**

```json
{
  "$edgeAgent": {
    "properties.desired": {
      "systemModules": {
        "edgeAgent": {
          "env": {
            "DEFAULT_IMAGE_UPDATE_MODE": {
              "value": "on_restart"
            }
          }
        }
      },
      "modules": {
        "myModule": {
          "version": "1.0",
          "type": "docker",
          "status": "running",
          "restartPolicy": "on-failure",
          "settings": {
            "image": "myregistry.azurecr.io/mymodule:latest",
            "createOptions": "{}"
          }
        }
      }
    }
  }
}
```

**With module-specific configuration:**

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

**With default configuration on $edgeAgent:**

```json
{
  "$edgeAgent": {
    "properties.desired": {
      "systemModules": {
        "edgeAgent": {
          "env": {
            "DEFAULT_IMAGE_UPDATE_MODE": {
              "value": "scheduled"
            },
            "DEFAULT_IMAGE_UPDATE_SCHEDULE": {
              "value": "02:00"
            }
          }
        }
      }
    }
  }
}
```

**With module-specific configuration:**

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

### Example 3: Mixed Policy (Different Modes Per Module)

**Combination of default and module-specific:**

```json
{
  "$edgeAgent": {
    "properties.desired": {
      "systemModules": {
        "edgeAgent": {
          "env": {
            "DEFAULT_IMAGE_UPDATE_MODE": {
              "value": "on_restart"
            }
          }
        }
      },
      "modules": {
        "criticalService": {
          "env": {
            "IMAGE_UPDATE_MODE": {
              "value": "immediate"
            }
          }
        },
        "batchProcessor": {
          "env": {
            "IMAGE_UPDATE_MODE": {
              "value": "scheduled"
            },
            "IMAGE_UPDATE_SCHEDULE": {
              "value": "23:00"
            }
          }
        },
        "manualModule": {
          "env": {
            "IMAGE_UPDATE_MODE": {
              "value": "on_request"
            }
          }
        },
        "standardModule": {
          // Uses default: on_restart
        }
      }
    }
  }
}
```

**Result:**
- `criticalService`: Updates immediately (module-specific override)
- `batchProcessor`: Updates at 23:00 daily (module-specific override)
- `manualModule`: Updates only on request (module-specific override)
- `standardModule`: Updates on restart (uses default from edgeAgent)

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

## Module Update Status (Reported Properties)

The Edge Agent automatically reports the update status of all modules to IoT Hub via reported properties. This provides visibility into the update lifecycle.

### Status Synchronization

- **Frequency**: Every **15 seconds**
- **Property**: `$edgeAgent` → `properties.reported.modules[moduleName].updateStatus`
- **Content**: State and update mode for each module (image and timestamp available via existing properties)

### Module States

Each module can be in one of three states:

| State | Description | When It Occurs |
|-------|-------------|----------------|
| **idle** | No update in progress | Module running with current image, no new deployment |
| **downloaded** | Image downloaded, not yet applied | New image pulled, waiting for trigger (on_restart/scheduled/on_request) |
| **applied** | Update applied, module restarted | Module updated with new image |

### Example Reported Properties

```json
{
  "properties": {
    "reported": {
      "modules": {
        "myModule": {
          "updateStatus": {
            "state": "downloaded",
            "updateMode": "on_restart"
          },
          "runtimeStatus": "running",
          "settings": {
            "image": "myregistry.azurecr.io/mymodule:2.0",
            "imageHash": "sha256:..."
          },
          "lastStartTimeUtc": "2026-07-15T10:30:45.123Z"
        },
        "criticalService": {
          "updateStatus": {
            "state": "applied",
            "updateMode": "immediate"
          },
          "runtimeStatus": "running",
          "settings": {
            "image": "myregistry.azurecr.io/critical:1.5",
            "imageHash": "sha256:..."
          },
          "lastStartTimeUtc": "2026-07-15T10:25:12.456Z"
        },
        "scheduledModule": {
          "updateStatus": {
            "state": "idle",
            "updateMode": "scheduled"
          },
          "runtimeStatus": "running",
          "settings": {
            "image": "myregistry.azurecr.io/scheduled:1.2",
            "imageHash": "sha256:..."
          },
          "lastStartTimeUtc": "2026-07-15T10:20:00.789Z"
        }
      },
      "updateTriggers": {
        "myModule": {
          "requestUpdate": true,
          "timestamp": "2026-07-15T10:00:00Z",
          "acknowledged": "2026-07-15T10:00:15Z",
          "status": "pending"
        }
      }
    }
  }
}s.*.updateStatus'

# Get status for specific module
az iot hub module-twin show \
  --hub-name <your-hub> \
  --device-id <your-device> \
  --module-id '$edgeAgent' \
  --query 'properties.reported.modules.myModule.updateStatus
3. Go to "Module Identity Twin"
4. View `properties.reported.moduleUpdateStatus`

**From Azure CLI:**
```bash
# Get all module update statuses
az iot hub module-twin show \
  --hub-name <your-hub> \
  --device-id <your-device> \
  --module-id '$edgeAgent' \
  --query 'properties.reported.moduleUpdateStatus'

# Get status for specific module
az iot hub module-twin show \
  --hub-name <your-hub> \
  --device-id <your-device> \
  --module-id '$edgeAgent' \
  --query 'properties.reported.moduleUpdateStatus.myModule'
```

### Use Cases

- **Monitoring**: Track which modules have downloaded updates but not yet applied them
- **Compliance**: Verify update timing policies are being followed
- **Troubleshooting**: Identify modules stuck in "downloaded" state
- **Orchestration**: External systems can monitor update progress and coordinate multi-device updates
- **Alerting**: Set up alerts when critical modules remain in "downloaded" state for too long

### Structure Optimization

The `updateStatus` is embedded directly within each module's reported properties to:
- ✅ **Avoid duplication**: Image name already available in `settings.image`
- ✅ **Avoid redundancy**: Timestamp available in `$metadata.$lastUpdated` and `lastStartTimeUtc`
- ✅ **Minimize twin size**: Saves ~1.8 KB for 21 modules (~75% reduction vs separate object)
- ✅ **Logical grouping**: Update status belongs with module data

This optimization is critical when approaching the **32 KB twin size limit**, especially in deployments with many modules.

## Implementation Details

### Architecture

The timing control is implemented via:

1. **UpdateScheduleManager** - Core logic for determining if/when updates should occur
2. **EdgeAgentConnection** - Processes `DEFAULT_IMAGE_UPDATE_MODE` and `DEFAULT_IMAGE_UPDATE_SCHEDULE` from `$edgeAgent` environment variables
3. **HealthRestartPlanner modifications** - Integrates schedule manager into planning phase
4. **Environment variable resolution** - Supports module-specific overrides and default configuration

### Configuration Resolution Flow

```
Deployment Update Received
    ↓
EdgeAgentConnection.ProcessDefaultConfiguration()
    ├─ Read DEFAULT_IMAGE_UPDATE_MODE from $edgeAgent env
    ├─ Read DEFAULT_IMAGE_UPDATE_SCHEDULE from $edgeAgent env
    └─ Call UpdateScheduleManager.SetDefaultConfiguration()
        ↓
HealthRestartPlanner.PlanAsync()
    ↓
ProcessAddedUpdatedModules()
    ├─ For each module:
    │   ├─ UpdateScheduleManager.GetUpdateMode(module)
    │   │   ├─ 1. Check module IMAGE_UPDATE_MODE env var
    │   │   ├─ 2. Check default from edgeAgent (DEFAULT_IMAGE_UPDATE_MODE)
    │   │   └─ 3. Return "immediate" (hardcoded default)
    │   ├─ Ask: "Should prepare (pull)?"
    │   ├─ Ask: "Should apply?"
    │   └─ Generate commands (NullCommand if no action needed)
    ↓
Commands Executed
```

### State Tracking

- **Last Update Attempt**: Prevents repeated updates within same hour for scheduled mode
- **Runtime Module Status**: Checks if module is currently running
- **Module Restart Detection**: Monitors LastStartTimeUtc to detect recent restarts (within 1 minute)
- **Default Configuration**: Cached from edgeAgent environment variables, updated on deployment changes

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

- **Invalid IMAGE_UPDATE_MODE**: Defaults to configured default or `immediate` with warning logged
- **Invalid IMAGE_UPDATE_SCHEDULE format**: Falls back to `no update` for that cycle with warning logged (accepted formats: `HH:mm` or full ISO 8601 date/time)
- **Missing schedule for scheduled mode**: Logs warning, skips updates for that module
- **Runtime errors**: Defaults to configured default or `immediate` to avoid blocking updates

## Future Enhancements

1. ✅ **Direct method** on edgeAgent to trigger on_request updates (Implemented in v1.6.1)
2. ✅ **Desired properties** support for triggering updates (Implemented in v1.6.1)
3. ✅ **Update status reporting** in module twins via reported properties (Implemented in v1.7.3)
4. ✅ **Default configuration** via edgeAgent environment variables (Implemented in v1.5.5)
5. **Cron-like scheduling** with more complex time patterns
6. **Rate limiting** for scheduled mode (e.g., max 1 update per day)
7. **Rollback triggers** if module health degrades after update
