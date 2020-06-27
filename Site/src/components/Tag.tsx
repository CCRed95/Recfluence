
import React, { useState, PropsWithChildren, MouseEventHandler } from "react"
import styled from 'styled-components'

const TagStyle = styled.span`
  background-color: rgb(66, 66, 66);
  font-size: 0.9em;
  font-weight: bold;
  line-height: 2;
  border-radius: 5px;
  padding: 3px 6px;
  white-space:nowrap;
`

export const Tag = ({ color, label, style }: { color?: string, label: string, style?: React.CSSProperties }) =>
  <TagStyle style={{ ...style, backgroundColor: color }}>{label}</TagStyle>