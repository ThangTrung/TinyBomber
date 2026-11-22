using UnityEngine;

public class RemotePlayerController : MonoBehaviour
{
    private const double AnimationHoldSeconds = 0.10;

    private Vector2? targetPosition;
    private double lastTargetChangeTime = double.NegativeInfinity;
    private Animator anim;
    private SpriteRenderer sr;

    private void Start()
    {
        anim = GetComponent<Animator>();
        sr = GetComponent<SpriteRenderer>();
        targetPosition ??= (Vector2)transform.position;
    }

    public void UpdatePosition(Vector2 newPos)
    {
        targetPosition ??= (Vector2)transform.position;
        if (newPos.Equals(targetPosition.Value)) return;

        targetPosition = newPos;
        lastTargetChangeTime = Time.unscaledTimeAsDouble;
    }

    private void Update()
    {
        Vector2 target = targetPosition.Value;
        bool chasingTarget = Vector2.Distance(transform.position, target) > 0.05f;

        if (chasingTarget)
        {
            float dirX = target.x - transform.position.x;
            if (Mathf.Abs(dirX) > 0.01f)
            {
                sr.flipX = dirX < 0;
            }

            transform.position = Vector2.MoveTowards(transform.position, target, 15f * Time.deltaTime);
        }

        bool hasRecentMovement = Time.unscaledTimeAsDouble - lastTargetChangeTime < AnimationHoldSeconds;
        anim.SetBool("IsMoving", chasingTarget || hasRecentMovement);
    }
}
