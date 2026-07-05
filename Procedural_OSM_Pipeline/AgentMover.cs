using UnityEngine;
using UnityEngine.InputSystem;

public class AgentMover : MonoBehaviour
{
    public float speed = 10f; // metres per second
    public float heightOffset = 1.5f;
    
    void Update()
    {
        float h = 0f;
        float v = 0f;

        if (Keyboard.current != null)
        {
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) h += 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) h -= 1f;
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) v += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) v -= 1f;
        }

        Vector3 move = new Vector3(h, 0, v).normalized * speed * Time.deltaTime;
        if (move.magnitude > 0)
        {
            transform.Translate(move, Space.World); // XZ plane
        }

        // Snap to terrain
        var ray = new Ray(new Vector3(transform.position.x, 10000f, transform.position.z), Vector3.down);
        RaycastHit[] hits = Physics.RaycastAll(ray, 20000f);
        foreach (var hit in hits)
        {
            if (hit.collider.GetComponent<TerrainGenerator>() != null || hit.collider.gameObject.name.Contains("Terrain"))
            {
                transform.position = new Vector3(transform.position.x, hit.point.y + heightOffset, transform.position.z);
                break;
            }
        }
    }
}
