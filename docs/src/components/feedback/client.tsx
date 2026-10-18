import type { FeedbackBlockProps } from 'fumadocs-core/mdx-plugins/remark-feedback-block';
import { buttonVariants } from 'fumadocs-ui/components/ui/button';
import {
  Collapsible,
  CollapsibleContent,
} from 'fumadocs-ui/components/ui/collapsible';
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from 'fumadocs-ui/components/ui/popover';
import { MessageSquare, ThumbsDown, ThumbsUp } from 'lucide-react';
import {
  type ReactNode,
  type SyntheticEvent,
  useEffect,
  useEffectEvent,
  useState,
  useTransition,
} from 'react';
import { useLocation } from 'react-router';
import { z } from 'zod/mini';
import { cn } from '@/lib/cn';
import {
  type ActionResponse,
  actionResponse,
  type BlockFeedback,
  blockFeedback,
  type PageFeedback,
  pageFeedback,
} from './schema';

// i18n translations for feedback component
const translations = {
  en: {
    howIsThisGuide: 'How is this guide?',
    good: 'Good',
    bad: 'Bad',
    thankYou: 'Thank you for your feedback!',
    viewOnGitHub: 'View on GitHub',
    submitAgain: 'Submit Again',
    leaveFeedback: 'Leave your feedback...',
    submit: 'Submit',
    feedback: 'Feedback',
    failed:
      'Your feedback could not be sent. Please try again later, or open an issue on GitHub.',
  },
  zh: {
    howIsThisGuide: '这篇文档如何？',
    good: '好',
    bad: '差',
    thankYou: '感谢您的反馈！',
    viewOnGitHub: '在 GitHub 上查看',
    submitAgain: '再次提交',
    leaveFeedback: '请留下您的反馈...',
    submit: '提交',
    feedback: '反馈',
    failed: '反馈发送失败。请稍后重试，或在 GitHub 上提交 issue。',
  },
} as const;

function useLocale() {
  const { pathname } = useLocation();
  return pathname.startsWith('/zh') ? 'zh' : 'en';
}

function useTranslations() {
  const locale = useLocale();
  return translations[locale];
}

function rateButtonVariants({ active }: { active: boolean }) {
  return cn(
    'inline-flex items-center gap-2 px-3 py-2 rounded-full font-medium border text-sm [&_svg]:size-4 disabled:cursor-not-allowed',
    active
      ? 'bg-fd-accent text-fd-accent-foreground [&_svg]:fill-current'
      : 'text-fd-muted-foreground',
  );
}

const pageFeedbackResult = z.extend(pageFeedback, {
  response: actionResponse,
});

const blockFeedbackResult = z.extend(blockFeedback, {
  response: actionResponse,
});

function normalizeFeedbackPath(pathname: string) {
  return pathname.replace(/\/$/, '') || '/';
}

/**
 * A feedback component to be attached at the end of page
 */
export function Feedback({
  onSendAction,
}: {
  onSendAction: (feedback: PageFeedback) => Promise<ActionResponse>;
}) {
  const { pathname } = useLocation();
  const url = normalizeFeedbackPath(pathname);
  const t = useTranslations();
  const { previous, setPrevious } = useSubmissionStorage(url, (v) => {
    const result = pageFeedbackResult.safeParse(v);
    return result.success ? result.data : null;
  });
  const [opinion, setOpinion] = useState<'good' | 'bad' | null>(null);
  const [message, setMessage] = useState('');
  const [isPending, startTransition] = useTransition();

  function submit(e?: SyntheticEvent) {
    if (opinion == null) return;

    startTransition(async () => {
      const feedback: PageFeedback = {
        url,
        opinion,
        message,
      };

      const response = await onSendAction(feedback);
      setPrevious({
        response,
        ...feedback,
      });
      setMessage('');
      setOpinion(null);
    });

    e?.preventDefault();
  }

  const activeOpinion = previous?.opinion ?? opinion;

  return (
    <Collapsible
      open={opinion !== null || previous !== null}
      onOpenChange={(v) => {
        if (!v) setOpinion(null);
      }}
      className="border-y py-3"
    >
      <div className="flex flex-row items-center gap-2">
        <p className="text-sm font-medium pe-2">{t.howIsThisGuide}</p>
        <button
          type="button"
          disabled={previous !== null}
          className={cn(
            rateButtonVariants({
              active: activeOpinion === 'good',
            }),
          )}
          onClick={() => {
            setOpinion('good');
          }}
        >
          <ThumbsUp />
          {t.good}
        </button>
        <button
          type="button"
          disabled={previous !== null}
          className={cn(
            rateButtonVariants({
              active: activeOpinion === 'bad',
            }),
          )}
          onClick={() => {
            setOpinion('bad');
          }}
        >
          <ThumbsDown />
          {t.bad}
        </button>
      </div>
      <CollapsibleContent className="mt-3">
        {previous ? (
          <div className="px-3 py-6 flex flex-col items-center gap-3 bg-fd-card text-fd-muted-foreground text-sm text-center rounded-xl">
            <p>{previous.response?.githubUrl ? t.thankYou : t.failed}</p>
            <div className="flex flex-row items-center gap-2">
              <a
                hidden={!previous.response?.githubUrl}
                href={previous.response?.githubUrl}
                rel="noreferrer noopener"
                target="_blank"
                className={cn(
                  buttonVariants({
                    color: 'primary',
                  }),
                  'text-xs',
                )}
              >
                {t.viewOnGitHub}
              </a>

              <button
                type="button"
                className={cn(
                  buttonVariants({
                    color: 'secondary',
                  }),
                  'text-xs',
                )}
                onClick={() => {
                  setOpinion(previous.opinion);
                  setPrevious(null);
                }}
              >
                {t.submitAgain}
              </button>
            </div>
          </div>
        ) : (
          <form className="flex flex-col gap-3" onSubmit={submit}>
            <textarea
              required
              value={message}
              onChange={(e) => setMessage(e.target.value)}
              className="border rounded-lg bg-fd-secondary text-fd-secondary-foreground p-3 resize-none focus-visible:outline-none placeholder:text-fd-muted-foreground"
              placeholder={t.leaveFeedback}
              onKeyDown={(e) => {
                if (!e.shiftKey && e.key === 'Enter') {
                  submit(e);
                }
              }}
            />
            <button
              type="submit"
              className={cn(buttonVariants({ color: 'outline' }), 'w-fit px-3')}
              disabled={isPending}
            >
              {t.submit}
            </button>
          </form>
        )}
      </CollapsibleContent>
    </Collapsible>
  );
}

/**
 * A feedback component for each content block in page, should be used with `remark-feedback-block`.
 *
 * See https://fumadocs.dev/docs/integrations/feedback.
 */
export function FeedbackBlock({
  id,
  body,
  onSendAction,
  children,
}: FeedbackBlockProps & {
  onSendAction: (feedback: BlockFeedback) => Promise<ActionResponse>;
  children: ReactNode;
}) {
  const { pathname } = useLocation();
  const url = normalizeFeedbackPath(pathname);
  const t = useTranslations();
  const blockId = `${url}-${id}`;
  const { previous, setPrevious } = useSubmissionStorage(blockId, (v) => {
    const result = blockFeedbackResult.safeParse(v);
    if (result.success) return result.data;
    return null;
  });
  const [message, setMessage] = useState('');
  const [isPending, startTransition] = useTransition();
  const [open, setOpen] = useState(false);

  function submit(e?: SyntheticEvent) {
    startTransition(async () => {
      const feedback: BlockFeedback = {
        blockId,
        blockBody: body?.slice(0, 2000),
        url,
        message,
      };

      const response = await onSendAction(feedback);
      setPrevious({
        response,
        ...feedback,
      });
      setMessage('');
    });

    e?.preventDefault();
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <div className="relative group/feedback">
        <div
          className={cn(
            'absolute -inset-1 rounded-sm pointer-events-none z-[-1]',
            open ? 'bg-fd-accent' : 'group-hover/feedback:bg-fd-accent',
          )}
        />
        <PopoverTrigger
          className={cn(
            buttonVariants({ color: 'secondary', size: 'sm' }),
            'absolute -top-7 end-0 backdrop-blur-sm text-fd-muted-foreground gap-1.5',
            !open && 'invisible group-hover/feedback:visible hover:visible',
          )}
          onClick={(e) => {
            setOpen((prev) => !prev);
            e.stopPropagation();
            e.preventDefault();
          }}
        >
          <MessageSquare className="size-3.5" />
          {t.feedback}
        </PopoverTrigger>

        <div className="[.prose-no-margin_&]:prose-no-margin">{children}</div>
      </div>

      <PopoverContent className="min-w-[300px]">
        {previous ? (
          <div className="flex flex-col items-center gap-2 text-fd-muted-foreground text-sm text-center rounded-xl">
            <p>{previous.response?.githubUrl ? t.thankYou : t.failed}</p>
            <div className="flex flex-row items-center gap-2">
              <a
                hidden={!previous.response?.githubUrl}
                href={previous.response?.githubUrl}
                rel="noreferrer noopener"
                target="_blank"
                className={cn(
                  buttonVariants({
                    color: 'primary',
                  }),
                  'text-xs',
                )}
              >
                {t.viewOnGitHub}
              </a>

              <button
                type="button"
                className={cn(
                  buttonVariants({
                    color: 'secondary',
                  }),
                  'text-xs',
                )}
                onClick={() => {
                  setPrevious(null);
                }}
              >
                {t.submitAgain}
              </button>
            </div>
          </div>
        ) : (
          <form className="flex flex-col gap-2" onSubmit={submit}>
            <textarea
              required
              value={message}
              onChange={(e) => setMessage(e.target.value)}
              className="border rounded-lg bg-fd-secondary text-fd-secondary-foreground p-3 resize-none focus-visible:outline-none placeholder:text-fd-muted-foreground"
              placeholder={t.leaveFeedback}
              onKeyDown={(e) => {
                if (!e.shiftKey && e.key === 'Enter') {
                  submit(e);
                }
              }}
            />
            <button
              type="submit"
              className={cn(buttonVariants({ color: 'outline' }), 'w-fit px-3')}
              disabled={isPending}
            >
              {t.submit}
            </button>
          </form>
        )}
      </PopoverContent>
    </Popover>
  );
}

function useSubmissionStorage<Result>(
  blockId: string,
  validate: (v: unknown) => Result | null,
) {
  const storageKey = `docs-feedback-${blockId}`;
  const [value, setValue] = useState<Result | null>(null);
  const validateCallback = useEffectEvent(validate);

  useEffect(() => {
    try {
      const item = localStorage.getItem(storageKey);
      if (item === null) return;
      const validated = validateCallback(JSON.parse(item));
      if (validated !== null) setValue(validated);
    } catch {
      // storage unavailable or corrupt: start without a previous submission
    }
  }, [storageKey]);

  return {
    previous: value,
    setPrevious(result: Result | null) {
      try {
        if (result) localStorage.setItem(storageKey, JSON.stringify(result));
        else localStorage.removeItem(storageKey);
      } catch {
        // storage unavailable: keep the result for this visit only
      }

      setValue(result);
    },
  };
}
