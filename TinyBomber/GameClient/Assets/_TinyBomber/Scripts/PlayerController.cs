using UnityEngine;
using GameShared;

public class PlayerController : MonoBehaviour
{
    public float speed = 5f;
    private Animator anim;
    private SpriteRenderer sr;
    private Vector2 moveDir;

    private void Start()
    {
        anim = GetComponent<Animator>();
        sr = GetComponent<SpriteRenderer>();
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
        bool moving = target != start;
        anim.SetBool("IsMoving", moving);
        if (!moving) return;

        if (target.x != start.x) sr.flipX = target.x < start.x;
        transform.position = target;
        ClientSend.SendPlayerMovement(target);
    }
}
