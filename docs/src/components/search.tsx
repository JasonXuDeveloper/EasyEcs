import { useStaticSearch } from 'fumadocs-core/search/client';
import {
  SearchDialog,
  SearchDialogClose,
  SearchDialogContent,
  SearchDialogHeader,
  SearchDialogIcon,
  SearchDialogInput,
  SearchDialogList,
  SearchDialogOverlay,
  type SharedProps,
} from 'fumadocs-ui/components/dialog/search';
import { useI18n } from 'fumadocs-ui/contexts/i18n';
import { searchParams } from '@/lib/search';

/**
 * Searches the index prerendered at /api/search/{lang}.json in the browser,
 * the current language's only, with page titles weighted (src/lib/search.ts).
 */
export default function EasyEcsSearchDialog(props: SharedProps) {
  const { locale } = useI18n();
  const search = useStaticSearch({
    from: `/api/search/${locale}.json`,
    locale,
    search: searchParams,
  });

  return (
    <SearchDialog {...search} {...props}>
      <SearchDialogOverlay />
      <SearchDialogContent>
        <SearchDialogHeader>
          <SearchDialogIcon />
          <SearchDialogInput />
          <SearchDialogClose />
        </SearchDialogHeader>
        <SearchDialogList />
      </SearchDialogContent>
    </SearchDialog>
  );
}
