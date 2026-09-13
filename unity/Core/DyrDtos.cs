using System;
using UnityEngine;

// DTOs mirroring the backend JSON (v0.5.0). JsonUtility ignores fields not declared here.

/// <summary>Bounding box normalised 0-1 in image space, origin top-left.</summary>
[Serializable]
public class BoxDto
{
    public float x0;
    public float y0;
    public float x1;
    public float y1;

    public float Width { get { return Mathf.Max(0f, x1 - x0); } }
    public float Height { get { return Mathf.Max(0f, y1 - y0); } }
    public bool IsValid { get { return Width > 0.01f && Height > 0.01f; } }
}

/// <summary>One AR label to draw: what it says and where it goes.</summary>
[Serializable]
public class ObjectLabelDto
{
    public string entity_id;
    public string name;
    public string title;
    public string body;
    public string[] steps;
    public BoxDto box;
    public bool is_new;              // first time this object was ever documented
    public string memory_summary;
    public string anchor_uuid;       // set once Unity has anchored it
    public string anchor_kind;       // "" | "qr" | "spatial" | "mruk"

    public bool HasAnchor { get { return !string.IsNullOrEmpty(anchor_uuid); } }
}

/// <summary>One streamed label or completion event.</summary>
[Serializable]
public class ScanStreamLineDto
{
    public string t;                 // "label" | "done" | "error"
    public ObjectLabelDto label;
    public ScanDoneDto done;
    public string error;
}

[Serializable]
public class ScanDoneDto
{
    public bool ok;
    public string reason;
}

[Serializable]
public class ObjectListResponseDto
{
    public ObjectLabelDto[] objects;
}

[Serializable]
public class AnchorBindRequestDto
{
    public string anchor_uuid;
    public string anchor_kind;
    public string room_id;
}

[Serializable]
public class HealthDto
{
    public bool ok;
    public string llm_base_url;
    public string llm_model;
    public string llm_state;            // "up" | "down" | "untested"
    public float llm_probe_latency_s;
    public string llm_error;
    public int objects;
}
