import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Markdown } from '../Markdown';

describe('Markdown', () => {
  it('renders headings, lists, code and GFM tables', () => {
    render(<Markdown content={'# Notes\n\n- **Healthy**\n\n```sql\nSELECT 1\n```\n\n| Service | Status |\n| --- | --- |\n| API | OK |'} />);
    expect(screen.getByRole('heading', { name: 'Notes' })).toBeInTheDocument();
    expect(screen.getByRole('list')).toHaveTextContent('Healthy');
    expect(screen.getByRole('table')).toHaveTextContent('API');
    expect(screen.getByText('SELECT 1')).toBeInTheDocument();
  });

  it('does not render raw HTML or dangerous URLs', () => {
    const { container } = render(<Markdown content={'<script>alert(1)</script>\n\n<img src=x onerror=alert(1)>\n\n[bad](javascript:alert)\n\n[safe](https://example.com)'} />);
    expect(container.querySelector('script')).toBeNull();
    expect(container.querySelector('img')).toBeNull();
    expect(screen.getByText('bad')).not.toHaveAttribute('href', 'javascript:alert');
    expect(screen.getByRole('link', { name: 'safe' })).toHaveAttribute('rel', 'noopener noreferrer');
  });
});
