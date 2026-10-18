import type {
  ActionResponse,
  BlockFeedback,
  PageFeedback,
} from '@/components/feedback/schema';

type FeedbackPayload =
  | { type: 'page'; feedback: PageFeedback }
  | { type: 'block'; feedback: BlockFeedback };

async function sendFeedback(payload: FeedbackPayload): Promise<ActionResponse> {
  const response = await fetch('/api/feedback', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(payload),
  });

  if (!response.ok) return { githubUrl: undefined };

  return (await response.json()) as ActionResponse;
}

export function handlePageFeedback(
  feedback: PageFeedback,
): Promise<ActionResponse> {
  return sendFeedback({ type: 'page', feedback });
}

export function handleBlockFeedback(
  feedback: BlockFeedback,
): Promise<ActionResponse> {
  return sendFeedback({ type: 'block', feedback });
}
