import type { FeedbackBlockProps } from 'fumadocs-core/mdx-plugins/remark-feedback-block';
import { Accordion, Accordions } from 'fumadocs-ui/components/accordion';
import { Card, Cards } from 'fumadocs-ui/components/card';
import { File, Files, Folder } from 'fumadocs-ui/components/files';
import { ImageZoom } from 'fumadocs-ui/components/image-zoom';
import { Step, Steps } from 'fumadocs-ui/components/steps';
import { Tab, Tabs } from 'fumadocs-ui/components/tabs';
import { TypeTable } from 'fumadocs-ui/components/type-table';
import defaultMdxComponents from 'fumadocs-ui/mdx';
import type { MDXComponents } from 'mdx/types';
import type { ComponentProps, ReactNode } from 'react';
import {
  BenchSource,
  BenchTable,
  BenchValue,
  FeatureComparison,
  FeatureCount,
} from '@/components/bench-data';
import { BenchChart, LeadChart, PeerChart } from '@/components/charts';
import { EcsDemo } from '@/components/demo';
import { handleBlockFeedback } from '@/components/feedback/actions';
import { FeedbackBlock } from '@/components/feedback/client';
import { Mermaid } from '@/components/mermaid';
import { Stat, Stats } from '@/components/stats';

export function getMDXComponents(components?: MDXComponents): MDXComponents {
  return {
    ...defaultMdxComponents,
    img: (props: ComponentProps<'img'>) => (
      <ImageZoom {...(props as ComponentProps<typeof ImageZoom>)} />
    ),
    Accordion,
    Accordions,
    BenchChart,
    BenchSource,
    BenchTable,
    BenchValue,
    Card,
    Cards,
    EcsDemo,
    FeatureComparison,
    FeatureCount,
    File,
    Files,
    Folder,
    LeadChart,
    Mermaid,
    PeerChart,
    Stat,
    Stats,
    Step,
    Steps,
    Tab,
    Tabs,
    TypeTable,
    FeedbackBlock: ({
      children,
      ...props
    }: FeedbackBlockProps & { children: ReactNode }) => (
      <FeedbackBlock {...props} onSendAction={handleBlockFeedback}>
        {children}
      </FeedbackBlock>
    ),
    ...components,
  };
}
