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
        float x = Input.GetAxisRaw("Horizontal");
        float y = Input.GetAxisRaw("Vertical");
        moveDir = new Vector2(x, y).normalized;

        if (moveDir != Vector2.zero)
        {
            if (x < 0) sr.flipX = true;
            else if (x > 0) sr.flipX = false;

            anim.SetBool("IsMoving", true);
        }
        else
        {
            anim.SetBool("IsMoving", false);
        }
    }

    private void FixedUpdate()
    {
        if (moveDir != Vector2.zero)
        {
            transform.Translate(moveDir * speed * Time.fixedDeltaTime);
            ClientSend.SendPlayerMovement(transform.position);
        }
    }
}