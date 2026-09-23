using UnityEngine;

public class RemotePlayerController : MonoBehaviour
{
    private Vector2 targetPosition;
    private Animator anim;
    private SpriteRenderer sr;

    private void Start()
    {
        anim = GetComponent<Animator>();
        sr = GetComponent<SpriteRenderer>();
        targetPosition = transform.position;
    }
    public void UpdatePosition(Vector2 newPos)
    {
        targetPosition = newPos;
    }

    private void Update()
    {
        float distance = Vector2.Distance(transform.position, targetPosition);

        if (distance > 0.05f)
        {
            float dirX = targetPosition.x - transform.position.x;
            if (Mathf.Abs(dirX) > 0.01f)
            {
                sr.flipX = dirX < 0;
            }

            transform.position = Vector2.MoveTowards(transform.position, targetPosition, 15f * Time.deltaTime);

            anim.SetBool("IsMoving", true);
        }
        else
        {
            anim.SetBool("IsMoving", false);
        }
    }
}