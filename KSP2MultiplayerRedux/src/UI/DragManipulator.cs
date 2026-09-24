using UnityEngine;
using UnityEngine.UIElements;

namespace KSP2MultiplayerRedux.UI
{

public class DragManipulator : PointerManipulator
{
    private Vector2 targetStartPosition;
    private Vector3 pointerStartPosition;
    private bool isDragging;

    public DragManipulator(VisualElement target)
    {
        this.target = target;
    }

    protected override void RegisterCallbacksOnTarget()
    {
        target.RegisterCallback<PointerDownEvent>(OnPointerDown);
        target.RegisterCallback<PointerMoveEvent>(OnPointerMove);
        target.RegisterCallback<PointerUpEvent>(OnPointerUp);
        target.RegisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
    }

    protected override void UnregisterCallbacksFromTarget()
    {
        target.UnregisterCallback<PointerDownEvent>(OnPointerDown);
        target.UnregisterCallback<PointerMoveEvent>(OnPointerMove);
        target.UnregisterCallback<PointerUpEvent>(OnPointerUp);
        target.UnregisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
    }

    private void OnPointerDown(PointerDownEvent evt)
    {
        if (isDragging) return;
        targetStartPosition = new Vector2(target.resolvedStyle.left, target.resolvedStyle.top);
        pointerStartPosition = evt.position;
        target.CapturePointer(evt.pointerId);
        isDragging = true;
        evt.StopPropagation();
    }

    private void OnPointerMove(PointerMoveEvent evt)
    {
        if (!isDragging || !target.HasPointerCapture(evt.pointerId)) return;
        Vector3 delta = evt.position - pointerStartPosition;
        target.style.left = targetStartPosition.x + delta.x;
        target.style.top = targetStartPosition.y + delta.y;
        evt.StopPropagation();
    }

    private void OnPointerUp(PointerUpEvent evt)
    {
        if (!isDragging || !target.HasPointerCapture(evt.pointerId)) return;
        target.ReleasePointer(evt.pointerId);
        isDragging = false;
        evt.StopPropagation();
    }

    private void OnPointerCaptureOut(PointerCaptureOutEvent evt)
    {
        if (!isDragging) return;
        isDragging = false;
    }
}

}
