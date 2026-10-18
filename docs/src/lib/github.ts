import { createPrivateKey } from 'node:crypto';
import { Octokit } from 'octokit';
import type {
  ActionResponse,
  BlockFeedback,
  PageFeedback,
} from '@/components/feedback/schema';

// GitHub Discussion configuration
const owner = 'JasonXuDeveloper';
const repo = 'EasyEcs';
const DocsCategory = 'Docs Feedback'; // a Discussions category in JasonXuDeveloper/EasyEcs

interface GitHubEnv {
  GITHUB_APP_ID?: string;
  GITHUB_APP_PRIVATE_KEY?: string;
  GITHUB_APP_INSTALLATION_ID?: string;
  APP_ID?: string;
  APP_PRIVATE_KEY?: string;
  APP_INSTALLATION_ID?: string;
}

let _octokit: Octokit | undefined;

async function getOctokit(env: GitHubEnv = process.env): Promise<Octokit> {
  if (_octokit) return _octokit;

  const appId = env.GITHUB_APP_ID ?? env.APP_ID;
  const privateKey = normalizePrivateKey(
    env.GITHUB_APP_PRIVATE_KEY ?? env.APP_PRIVATE_KEY,
  );
  const installationId =
    env.GITHUB_APP_INSTALLATION_ID ?? env.APP_INSTALLATION_ID;

  if (!appId || !privateKey || !installationId) {
    throw new Error(
      'GitHub App credentials not configured. Set GITHUB_APP_ID, GITHUB_APP_PRIVATE_KEY, and GITHUB_APP_INSTALLATION_ID environment variables.',
    );
  }

  const { createAppAuth } = await import('@octokit/auth-app');

  _octokit = new Octokit({
    authStrategy: createAppAuth,
    auth: {
      appId,
      privateKey,
      installationId,
    },
  });

  return _octokit;
}

interface FeedbackDestination {
  repoId: string;
  categoryId: string;
}

function normalizePrivateKey(privateKey?: string) {
  const key = privateKey?.replace(/\\n/g, '\n');

  if (!key?.includes('-----BEGIN RSA PRIVATE KEY-----')) return key;

  return createPrivateKey(key).export({
    format: 'pem',
    type: 'pkcs8',
  }) as string;
}

let _feedbackDestination: FeedbackDestination | undefined;

async function getFeedbackDestination(
  env: GitHubEnv = process.env,
): Promise<FeedbackDestination> {
  if (_feedbackDestination) return _feedbackDestination;

  const octokit = await getOctokit(env);

  const result = await octokit.graphql<{
    repository: {
      id: string;
      discussionCategories: {
        nodes: Array<{ id: string; name: string }>;
      };
    };
  }>(
    `query {
      repository(owner: "${owner}", name: "${repo}") {
        id
        discussionCategories(first: 25) {
          nodes { id name }
        }
      }
    }`,
  );

  const category = result.repository.discussionCategories.nodes.find(
    (node) => node.name === DocsCategory,
  );

  if (!category) {
    throw new Error(
      `Discussion category "${DocsCategory}" not found. Please create it in your GitHub repository settings.`,
    );
  }

  _feedbackDestination = {
    repoId: result.repository.id,
    categoryId: category.id,
  };

  return _feedbackDestination;
}

async function createDiscussionThread(
  id: string,
  title: string,
  body: string,
  env: GitHubEnv = process.env,
): Promise<string | undefined> {
  const octokit = await getOctokit(env);
  const { repoId, categoryId } = await getFeedbackDestination(env);

  // Search for existing discussion
  const searchQuery = `repo:${owner}/${repo} "${id}" in:body`;
  const searchResult = await octokit.graphql<{
    search: {
      nodes: Array<{
        id: string;
        url: string;
      }>;
    };
  }>(
    `query($searchQuery: String!) {
      search(query: $searchQuery, type: DISCUSSION, first: 1) {
        nodes {
          ... on Discussion {
            id
            url
          }
        }
      }
    }`,
    { searchQuery },
  );

  const existing = searchResult.search.nodes[0];

  if (existing) {
    // Add comment to existing discussion
    const commentResult = await octokit.graphql<{
      addDiscussionComment: {
        comment: { url: string };
      };
    }>(
      `mutation($discussionId: ID!, $body: String!) {
        addDiscussionComment(input: { discussionId: $discussionId, body: $body }) {
          comment { url }
        }
      }`,
      { discussionId: existing.id, body },
    );

    return commentResult.addDiscussionComment.comment.url;
  }

  // Create new discussion
  const createResult = await octokit.graphql<{
    createDiscussion: {
      discussion: { url: string };
    };
  }>(
    `mutation($repoId: ID!, $categoryId: ID!, $title: String!, $body: String!) {
      createDiscussion(input: { repositoryId: $repoId, categoryId: $categoryId, title: $title, body: $body }) {
        discussion { url }
      }
    }`,
    { repoId, categoryId, title, body },
  );

  return createResult.createDiscussion.discussion.url;
}

/**
 * Handle page-level feedback
 */
export async function onPageFeedbackAction(
  feedback: PageFeedback,
  env: GitHubEnv = process.env,
): Promise<ActionResponse> {
  try {
    const id = `page-feedback:${feedback.url}`;
    const title = `Docs Feedback: ${feedback.url}`;
    const body = `**Page:** ${feedback.url}
**Opinion:** ${feedback.opinion === 'good' ? 'Good' : 'Bad'}

---

${feedback.message}

---
*Feedback ID: ${id}*`;

    const githubUrl = await createDiscussionThread(id, title, body, env);
    return { githubUrl };
  } catch (error) {
    console.error('Failed to create GitHub discussion:', error);
    return { githubUrl: undefined };
  }
}

/**
 * Handle block-level feedback (paragraph-specific)
 */
export async function onBlockFeedbackAction(
  feedback: BlockFeedback,
  env: GitHubEnv = process.env,
): Promise<ActionResponse> {
  try {
    const id = `block-feedback:${feedback.blockId}`;
    const title = `Docs Feedback: Block in ${feedback.url}`;
    const body = `**Page:** ${feedback.url}
**Block ID:** ${feedback.blockId}

**Referenced Text:**
> ${feedback.blockBody || 'N/A'}

---

${feedback.message}

---
*Feedback ID: ${id}*`;

    const githubUrl = await createDiscussionThread(id, title, body, env);
    return { githubUrl };
  } catch (error) {
    console.error('Failed to create GitHub discussion:', error);
    return { githubUrl: undefined };
  }
}
