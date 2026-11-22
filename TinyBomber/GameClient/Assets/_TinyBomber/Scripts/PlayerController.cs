using UnityEngine;
using System;

public class PlayerController : MonoBehaviour
{
    private static readonly Func<Vector2, bool> SendMovement = ClientSend.SendPlayerMovement;

    public float speed = 4f;
    private Animator anim;
    private SpriteRenderer sr;
    private Vector2 moveDir;
    private Action<Vector2, Vector2> applyPosition;

    private void Start()
    {
        anim = GetComponent<Animator>();
        sr = GetComponent<SpriteRenderer>();
        applyPosition = ApplyPosition;
    }

    private void Update()
    {
        if (!GameManager.instance.CanControl)
        {
            moveDir = Vector2.zero;
            return;
        }

        if (Input.GetKeyDown(KeyCode.Space)) ClientSend.PlaceBomb();
        float x = Input.GetAxisRaw("Horizontal");
        float y = Input.GetAxisRaw("Vertical");
        moveDir = new Vector2(x, y).normalized;
    }

    private void FixedUpdate()
    {
        if (!GameManager.instance.CanControl)
        {
            anim.SetBool("IsMoving", false);
            return;
        }

        Vector2 start = transform.position;
        Vector2 target = GameManager.instance.ResolveMovement(start, moveDir * speed * Time.fixedDeltaTime);
        if (!TryApplyMovement(start, target, applyPosition, SendMovement))
            anim.SetBool("IsMoving", false);
    }

    private void ApplyPosition(Vector2 start, Vector2 target)
    {
        anim.SetBool("IsMoving", true);
        if (target.x != start.x) sr.flipX = target.x < start.x;
        transform.position = target;
    }

    private static bool TryApplyMovement(Vector2 start, Vector2 target,
        Action<Vector2, Vector2> apply, Func<Vector2, bool> send)
    {
        // Match the resolver's exact comparison so even the last step at a tile edge is applied.
        if (target.Equals(start)) return false;
        if (!send(target)) return false;

        apply(start, target);
        return true;
    }
}
