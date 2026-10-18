import { z } from 'zod/mini';

// Length limits keep a single request from posting huge discussions.
const url = z.string().check(z.maxLength(300));
const message = z.string().check(z.minLength(1), z.maxLength(5000));

export const blockFeedback = z.object({
  url,
  blockId: z.string().check(z.maxLength(400)),
  message,

  /** the referenced text of block */
  blockBody: z.optional(z.string().check(z.maxLength(2000))),
});

export const pageFeedback = z.object({
  opinion: z.enum(['good', 'bad']),
  url,
  message,
});

export const actionResponse = z.object({
  githubUrl: z.optional(z.string()),
});

export type BlockFeedback = z.infer<typeof blockFeedback>;
export type PageFeedback = z.infer<typeof pageFeedback>;
export type ActionResponse = z.infer<typeof actionResponse>;
