using UnityEngine;
using GameShared;

public class PlayerController : MonoBehaviour
{
    public float speed = 5f;
    private Animator anim;
    private SpriteRenderer sr;

    private void Start()
    {
        anim = GetComponent<Animator>();
        sr = GetComponent<SpriteRenderer>();
    }

    private void Update()
    {
        float x = Input.GetAxisRaw("Horizontal");
        float y = Input.GetAxisRaw("Vertical");
        Vector2 moveDir = new Vector2(x, y).normalized;

        if (moveDir != Vector2.zero)
        {
            transform.Translate(moveDir * speed * Time.deltaTime);

            if (x < 0) sr.flipX = true;
            else if (x > 0) sr.flipX = false;

            anim.SetBool("IsMoving", true);

            ClientSend.SendPlayerMovement(transform.position);
        }
        else
        {
            // Tắt animation chạy
            anim.SetBool("IsMoving", false);
        }
    }
}